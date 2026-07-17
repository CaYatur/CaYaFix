// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaYaFix.Core;

public sealed class SessionManager : ISessionManager
{
    private const string SignaturePurpose = "session-manifest-v1";
    private const int EnvelopeSchemaVersion = 1;
    private const long MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumManifestRecords = 4_096;
    private const int MaximumSessionDirectories = 512;
    private const int MaximumTechnicalDetailCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _sessionsRoot;
    private readonly IIntegrityService _integrity;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SessionManager(string? dataRoot = null, IIntegrityService? integrity = null)
    {
        var root = Path.GetFullPath(dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CaYaFix"));
        _sessionsRoot = Path.Combine(root, "Sessions");
        Directory.CreateDirectory(_sessionsRoot);
        if (HasReparsePoint(_sessionsRoot))
        {
            throw new InvalidOperationException("The CaYaFix sessions directory cannot contain a reparse point.");
        }

        _integrity = integrity ?? new ProtectedIntegrityService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaYaFix"));
    }

    public async Task<SessionManifest> CreateAsync(bool dryRun, CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var id = $"{stamp}-{Guid.NewGuid().ToString("N")[..6]}";
        var directory = Path.Combine(_sessionsRoot, id);

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "Backups"));
        if (HasReparsePoint(directory))
        {
            throw new InvalidOperationException("The new session directory cannot contain a reparse point.");
        }

        var session = new SessionManifest
        {
            Id = id,
            DirectoryPath = directory,
            DryRun = dryRun
        };
        await SaveAsync(session, ct).ConfigureAwait(false);
        return session;
    }

    public async Task SaveAsync(SessionManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifestShape(manifest);
        var directory = ValidateSessionDirectory(manifest.DirectoryPath, manifest.Id);
        Directory.CreateDirectory(directory);
        if (HasReparsePoint(directory))
        {
            throw new InvalidOperationException("The session directory cannot contain a reparse point.");
        }

        var target = Path.Combine(directory, "manifest.json");
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $"manifest.{nonce}.tmp");
        var manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
        var envelope = new SignedManifestEnvelope
        {
            SchemaVersion = EnvelopeSchemaVersion,
            Manifest = manifest,
            Signature = _integrity.Sign(SignaturePurpose, manifestBytes)
        };
        var envelopeBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        if (envelopeBytes.LongLength > MaximumManifestBytes)
        {
            throw new InvalidOperationException("The session manifest exceeds the safe size limit.");
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureRegularOrMissing(target);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(envelopeBytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, true);
        }
        finally
        {
            TryDelete(temporary);
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<SessionManifest>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_sessionsRoot)) return [];

        var sessions = new List<SessionManifest>(MaximumSessionDirectories);
        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_sessionsRoot))
            {
                ct.ThrowIfCancellationRequested();
                if (!LooksLikeSessionDirectory(directory)) continue;

                directories.Add(directory);
                if (directories.Count > MaximumSessionDirectories)
                {
                    directories.Remove(directories.Min!);
                }
            }
        }
        catch (IOException)
        {
            return sessions;
        }
        catch (UnauthorizedAccessException)
        {
            return sessions;
        }

        foreach (var directory in directories.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var parsed = await TryLoadTrustedAsync(directory, ct).ConfigureAwait(false);
            if (parsed is not null) sessions.Add(parsed);
        }

        return sessions;
    }

    private static bool LooksLikeSessionDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length != 22 || name[8] != '-' || name[15] != '-') return false;

        for (var index = 0; index < name.Length; index++)
        {
            if (index is 8 or 15) continue;
            if (index < 15)
            {
                if (!char.IsAsciiDigit(name[index])) return false;
            }
            else if (!Uri.IsHexDigit(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<SessionManifest?> GetPendingAsync(CancellationToken ct)
    {
        var sessions = await ListAsync(ct).ConfigureAwait(false);
        return sessions.FirstOrDefault(SessionRecoveryGates.SessionRequiresRecovery) ??
               sessions.FirstOrDefault(session =>
                   session.PendingVerify || session.Status == SessionStatus.PendingVerification);
    }

    public async Task<bool> UndoActionAsync(
        SessionManifest session,
        int actionIndex,
        IBackupService backups,
        CancellationToken ct)
    {
        var trusted = await ReloadTrustedAsync(session, ct).ConfigureAwait(false);
        if (trusted is null || actionIndex < 0 || actionIndex >= trusted.Actions.Count) return false;

        var action = trusted.Actions[actionIndex];
        if (!action.Applied || action.Undone || action.Backup is null) return false;

        var restored = await backups.RestoreAsync(action.Backup, ct).ConfigureAwait(false);
        if (restored)
        {
            action.Undone = true;
            UpdateRecoveryState(trusted);
            await SaveAsync(trusted, ct).ConfigureAwait(false);
        }

        return restored;
    }

    public async Task<bool> DismissActionAsync(
        SessionManifest session,
        int actionIndex,
        CancellationToken ct)
    {
        var trusted = await ReloadTrustedAsync(session, ct).ConfigureAwait(false);
        if (trusted is null || actionIndex < 0 || actionIndex >= trusted.Actions.Count) return false;

        var action = trusted.Actions[actionIndex];
        if (!SessionRecoveryGates.RequiresRecovery(action)) return false;

        action.ResultMessageKey = "FixResult_RecoveryDismissed";
        action.TechnicalDetail = string.IsNullOrWhiteSpace(action.TechnicalDetail)
            ? "Recovery dismissed by the user."
            : action.TechnicalDetail + Environment.NewLine + "Recovery dismissed by the user.";
        UpdateRecoveryState(trusted);
        await SaveAsync(trusted, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DismissSessionRecoveryAsync(
        SessionManifest session,
        CancellationToken ct)
    {
        var trusted = await ReloadTrustedAsync(session, ct).ConfigureAwait(false);
        if (trusted is null) return false;

        var any = false;
        foreach (var action in trusted.Actions.Where(SessionRecoveryGates.RequiresRecovery))
        {
            action.ResultMessageKey = "FixResult_RecoveryDismissed";
            action.TechnicalDetail = string.IsNullOrWhiteSpace(action.TechnicalDetail)
                ? "Recovery dismissed by the user."
                : action.TechnicalDetail + Environment.NewLine + "Recovery dismissed by the user.";
            any = true;
        }

        if (!any) return false;
        UpdateRecoveryState(trusted);
        await SaveAsync(trusted, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UndoSessionAsync(
        SessionManifest session,
        IBackupService backups,
        CancellationToken ct)
    {
        var trusted = await ReloadTrustedAsync(session, ct).ConfigureAwait(false);
        if (trusted is null) return false;

        var success = true;
        for (var index = trusted.Actions.Count - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();
            var action = trusted.Actions[index];
            if (!action.Applied || action.Undone) continue;

            if (action.Backup is null || !await backups.RestoreAsync(action.Backup, ct).ConfigureAwait(false))
            {
                success = false;
                continue;
            }

            action.Undone = true;
            UpdateRecoveryState(trusted);
            await SaveAsync(trusted, ct).ConfigureAwait(false);
        }

        UpdateRecoveryState(trusted);
        await SaveAsync(trusted, ct).ConfigureAwait(false);

        return success;
    }

    private static void UpdateRecoveryState(SessionManifest session)
    {
        var active = session.Actions.Where(action => action.Applied && !action.Undone).ToArray();
        session.PendingVerify = active.Any(action => action.RequiresReboot && !action.Verified);
        session.RequiresReboot = session.PendingVerify;
        if (active.Length == 0)
        {
            session.Status = SessionStatus.RolledBack;
            session.CompletedAt = DateTimeOffset.Now;
        }
        else if (session.PendingVerify)
        {
            session.Status = SessionStatus.PendingVerification;
            session.CompletedAt = null;
        }
        else if (session.Status is SessionStatus.PendingVerification or SessionStatus.RolledBack)
        {
            session.Status = SessionStatus.Completed;
            session.CompletedAt = DateTimeOffset.Now;
        }
    }

    private async Task<SessionManifest?> ReloadTrustedAsync(SessionManifest session, CancellationToken ct)
    {
        try
        {
            var directory = ValidateSessionDirectory(session.DirectoryPath, session.Id);
            return await TryLoadTrustedAsync(directory, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<SessionManifest?> TryLoadTrustedAsync(string directory, CancellationToken ct)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");

        try
        {
            if (!File.Exists(manifestPath) || HasReparsePoint(directory) || IsReparsePoint(manifestPath)) return null;

            var length = new FileInfo(manifestPath).Length;
            if (length <= 0 || length > MaximumManifestBytes) return null;

            var bytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<SignedManifestEnvelope>(bytes, JsonOptions);
            if (envelope is null || envelope.SchemaVersion != EnvelopeSchemaVersion ||
                envelope.Manifest is null || string.IsNullOrWhiteSpace(envelope.Signature) ||
                envelope.Signature.Length > 512) return null;

            var parsed = envelope.Manifest;
            var canonical = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parsed, JsonOptions));
            if (!_integrity.Verify(SignaturePurpose, canonical, envelope.Signature)) return null;
            ValidateManifestShape(parsed);
            var validated = ValidateSessionDirectory(parsed.DirectoryPath, parsed.Id);
            return PathsEqual(validated, directory) ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string ValidateSessionDirectory(string directoryPath, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 ||
            id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException("The session identifier is invalid.");
        }

        var directory = Path.GetFullPath(directoryPath);
        var expected = Path.GetFullPath(Path.Combine(_sessionsRoot, id));
        if (!PathsEqual(directory, expected) || !IsUnderRoot(directory, _sessionsRoot))
        {
            throw new InvalidOperationException("The session directory is outside the trusted data root.");
        }

        return directory;
    }

    private static void ValidateManifestShape(SessionManifest manifest)
    {
        RequireText(manifest.Id, 64, "session identifier");
        RequireText(manifest.DirectoryPath, 32_767, "session directory");
        if (!Enum.IsDefined(manifest.Status) || manifest.Actions is null || manifest.Findings is null ||
            manifest.Actions.Count > MaximumManifestRecords || manifest.Findings.Count > MaximumManifestRecords)
        {
            throw new InvalidOperationException("The session manifest structure is invalid.");
        }

        foreach (var action in manifest.Actions)
        {
            if (action is null || !Enum.IsDefined(action.Tier) ||
                action.Verified && !action.Applied ||
                action.Undone && (!action.Applied || action.Backup is null) ||
                action.Applied && action.Backup is null && !action.BackupOptional ||
                action.BackupOptional && action.Tier != RiskTier.Aggressive)
            {
                throw new InvalidOperationException("The session action state is invalid.");
            }

            RequireText(action.FixId, 256, "repair identifier");
            RequireText(action.TitleMessageKey, 256, "repair title key", allowEmpty: true);
            RequireText(action.FindingCheckId, 256, "diagnostic identifier");
            RequireText(action.ResultMessageKey, 256, "result message key", allowEmpty: true);
            RequireText(action.TechnicalDetail, MaximumTechnicalDetailCharacters, "action detail", allowEmpty: true);
            if (action.Backup is not null) ValidateBackupShape(action.Backup);
        }

        foreach (var finding in manifest.Findings)
        {
            if (finding is null || !Enum.IsDefined(finding.Severity) || !Enum.IsDefined(finding.Status))
            {
                throw new InvalidOperationException("The session finding state is invalid.");
            }

            RequireText(finding.CheckId, 256, "diagnostic identifier");
            RequireText(finding.ModuleId, 128, "module identifier");
            RequireText(finding.ModuleName, 512, "module name", allowEmpty: true);
            RequireText(finding.MessageKey, 256, "finding message key");
            RequireText(finding.UserMessage, MaximumTechnicalDetailCharacters, "finding message", allowEmpty: true);
            RequireText(finding.TechnicalDetail, MaximumTechnicalDetailCharacters, "finding detail", allowEmpty: true);
        }
    }

    private static void ValidateBackupShape(BackupEntry backup)
    {
        if (!Enum.IsDefined(backup.Kind) || backup.Metadata is null || backup.Metadata.Count > 64)
        {
            throw new InvalidOperationException("The backup metadata is invalid.");
        }

        RequireText(backup.Id, 128, "backup identifier");
        RequireText(backup.Label, 4_096, "backup label");
        RequireText(backup.Location, 32_767, "backup location");
        if (backup.ContentHash is null || backup.ContentHash.Length != 64 ||
            backup.ContentHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The backup content hash is invalid.");
        }

        foreach (var pair in backup.Metadata)
        {
            RequireText(pair.Key, 256, "backup metadata key");
            RequireText(pair.Value, MaximumTechnicalDetailCharacters, "backup metadata value", allowEmpty: true);
        }
    }

    private static void RequireText(string? value, int maximumLength, string field, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximumLength || value.IndexOf('\0') >= 0 ||
            !allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"The {field} is invalid.");
        }
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasReparsePoint(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static void EnsureRegularOrMissing(string path)
    {
        if (File.Exists(path) && IsReparsePoint(path))
        {
            throw new InvalidOperationException("A session manifest cannot be a reparse point.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale temporary file is harmless and never trusted as a manifest.
        }
    }

    private sealed class SignedManifestEnvelope
    {
        public int SchemaVersion { get; init; }
        public SessionManifest? Manifest { get; init; }
        public string Signature { get; init; } = string.Empty;
    }
}
