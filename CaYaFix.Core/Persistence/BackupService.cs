// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CaYaFix.Core;

public sealed partial class BackupService : IBackupService
{
    private const long MaximumDirectoryBackupBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumSingleFileBackupBytes = 512L * 1024 * 1024;
    private const int MaximumBackupEntries = 250_000;
    private const long MaximumJsonBackupBytes = 16L * 1024 * 1024;
    private const int MaximumBundleEntries = 128;
    private const int MaximumBundleDepth = 8;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 32 };
    private static readonly HashSet<string> AllowedRestoreExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "netsh.exe", "pnputil.exe", "powercfg.exe", "powershell.exe", "reg.exe", "sc.exe"
    };

    private readonly ICommandRunner _commands;
    private readonly string _sessionsRoot;
    private readonly ConcurrentDictionary<string, Func<BackupEntry, CancellationToken, Task<bool>>> _restoreHandlers =
        new(StringComparer.Ordinal);

    public BackupService(ICommandRunner commands, string? dataRoot = null)
    {
        _commands = commands;
        var root = Path.GetFullPath(dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CaYaFix"));
        _sessionsRoot = Path.Combine(root, "Sessions");
        Directory.CreateDirectory(_sessionsRoot);
        if (HasReparsePointInExistingPath(_sessionsRoot))
        {
            throw new InvalidOperationException("The CaYaFix sessions path cannot contain a reparse point.");
        }
    }

    public void RegisterRestoreHandler(
        string id,
        Func<BackupEntry, CancellationToken, Task<bool>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(handler);
        if (id.Length > 80 || id.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '.'))
        {
            throw new ArgumentException("The restore handler identifier is invalid.", nameof(id));
        }
        if (!_restoreHandlers.TryAdd(id, handler))
        {
            throw new InvalidOperationException($"Restore handler '{id}' is already registered.");
        }
    }

    public async Task<BackupEntry?> CaptureRegistryAsync(string key, string directory, CancellationToken ct)
    {
        if (!RegistryKeyPattern().IsMatch(key)) return null;
        directory = PrepareDirectory(directory);
        var file = Path.Combine(directory, $"registry-{SafeName(key)}-{Guid.NewGuid():N}.reg");
        var result = await _commands.RunAsync(
            "reg.exe",
            ["export", key, file, "/y"],
            TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);

        return result.Success && File.Exists(file)
            ? await CreateFileEntryAsync(BackupKind.Registry, key, file, new() { ["key"] = key }, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<BackupEntry?> CaptureFileAsync(string path, string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path) || HasReparsePointInExistingPath(path)) return null;
        var sourceLength = new FileInfo(path).Length;
        if (sourceLength < 0 || sourceLength > MaximumSingleFileBackupBytes) return null;

        directory = PrepareDirectory(directory);
        var file = Path.Combine(directory, $"file-{SafeName(Path.GetFileName(path))}-{Guid.NewGuid():N}.bak");
        if (!await CopyFileBoundedAsync(path, file, MaximumSingleFileBackupBytes, ct).ConfigureAwait(false))
        {
            return null;
        }
        return await CreateFileEntryAsync(
            BackupKind.File,
            path,
            file,
            new() { ["originalPath"] = Path.GetFullPath(path) },
            ct).ConfigureAwait(false);
    }

    public async Task<BackupEntry?> CaptureCommandStateAsync(
        string label,
        string executable,
        IReadOnlyList<string> captureArguments,
        string restoreExecutable,
        IReadOnlyList<string> restoreArguments,
        string directory,
        CancellationToken ct)
    {
        if (!IsAllowedRestoreCommand(restoreExecutable, restoreArguments)) return null;
        directory = PrepareDirectory(directory);
        var result = await _commands.RunAsync(executable, captureArguments, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
        if (!result.Success) return null;

        var file = Path.Combine(directory, $"state-{SafeName(label)}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, result.StdOut, ct).ConfigureAwait(false);
        return await CreateFileEntryAsync(
            BackupKind.CommandState,
            label,
            file,
            RestoreMetadata(restoreExecutable, restoreArguments),
            ct).ConfigureAwait(false);
    }

    public async Task<BackupEntry?> CaptureServicesAsync(
        IEnumerable<string> serviceNames,
        string directory,
        CancellationToken ct)
    {
        var names = serviceNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0 || names.Length > 128 || names.Any(name => !IsSafeServiceName(name))) return null;

        var literals = string.Join(',', names.Select(name => $"'{name}'"));
        var script = $"Get-CimInstance Win32_Service | Where-Object {{ $_.Name -in @({literals}) }} | ForEach-Object {{$delayed=(Get-ItemProperty -LiteralPath (\"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\\"+$_.Name) -Name DelayedAutoStart -ErrorAction SilentlyContinue).DelayedAutoStart; [pscustomobject]@{{Name=$_.Name;StartMode=[string]$_.StartMode;State=[string]$_.State;DelayedAutoStart=([int]$delayed -eq 1)}}}}";
        var snapshots = await _commands.RunPsJsonAsync<List<ServiceSnapshot>>(script, ct).ConfigureAwait(false);
        if (snapshots is null || snapshots.Count != names.Length ||
            snapshots.Any(item => !IsSafeServiceName(item.Name)) ||
            names.Any(name => snapshots.All(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        directory = PrepareDirectory(directory);
        var file = Path.Combine(directory, $"services-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(snapshots, JsonOptions), ct).ConfigureAwait(false);
        return await CreateFileEntryAsync(
            BackupKind.ServiceState,
            string.Join(", ", names),
            file,
            new(),
            ct).ConfigureAwait(false);
    }

    public async Task<BackupEntry?> CaptureDriverAsync(string publishedInfName, string directory, CancellationToken ct)
    {
        if (!PublishedInfPattern().IsMatch(publishedInfName)) return null;

        directory = PrepareDirectory(directory);
        var driverDirectory = Path.Combine(directory, $"driver-{SafeName(publishedInfName)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(driverDirectory);
        var result = await _commands.RunAsync(
            "pnputil.exe",
            ["/export-driver", publishedInfName, driverDirectory],
            TimeSpan.FromMinutes(10),
            ct).ConfigureAwait(false);
        if (!result.Success || !EnumerateTrustedFiles(driverDirectory).Any(path => path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        FlushBackupContentToDisk(driverDirectory, isDirectory: true, ct);
        return new BackupEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = BackupKind.Driver,
            Label = publishedInfName,
            Location = driverDirectory,
            ContentHash = await HashDirectoryAsync(driverDirectory, ct).ConfigureAwait(false),
            Metadata = { ["publishedInfName"] = publishedInfName }
        };
    }

    public async Task<BackupEntry?> CaptureValueAsync(
        string label,
        object? value,
        string directory,
        CancellationToken ct)
    {
        directory = PrepareDirectory(directory);
        var file = Path.Combine(directory, $"value-{SafeName(label)}-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBackupBytes) return null;
        await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);
        return await CreateFileEntryAsync(BackupKind.Value, label, file, new(), ct).ConfigureAwait(false);
    }

    public async Task<BackupEntry?> CaptureExistingCommandStateAsync(
        string label,
        string path,
        string restoreExecutable,
        IReadOnlyList<string> restoreArguments,
        CancellationToken ct)
    {
        var isFile = File.Exists(path);
        var isDirectory = Directory.Exists(path);
        if ((!isFile && !isDirectory) || !IsTrustedBackupPath(path) || IsReparsePoint(path) ||
            !IsAllowedRestoreCommand(restoreExecutable, restoreArguments))
        {
            return null;
        }

        var metadata = RestoreMetadata(restoreExecutable, restoreArguments);
        metadata["contentType"] = isDirectory ? "directory" : "file";
        FlushBackupContentToDisk(path, isDirectory, ct);
        return new BackupEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = BackupKind.CommandState,
            Label = label,
            Location = Path.GetFullPath(path),
            ContentHash = isDirectory
                ? await HashDirectoryAsync(path, ct).ConfigureAwait(false)
                : await HashFileAsync(path, ct).ConfigureAwait(false),
            Metadata = metadata
        };
    }

    public async Task<BackupEntry?> CaptureBundleAsync(
        string label,
        IReadOnlyList<BackupEntry> entries,
        string directory,
        CancellationToken ct)
    {
        if (entries.Count == 0 || entries.Count > MaximumBundleEntries ||
            entries.Any(entry => !IsEntryDescriptorSafe(entry) || !IsTrustedBackupPath(entry.Location)))
        {
            return null;
        }

        directory = PrepareDirectory(directory);
        var file = Path.Combine(directory, $"bundle-{SafeName(label)}-{Guid.NewGuid():N}.json");
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBackupBytes) return null;
        await File.WriteAllTextAsync(file, json, ct).ConfigureAwait(false);
        return await CreateFileEntryAsync(BackupKind.Bundle, label, file, new(), ct).ConfigureAwait(false);
    }

    public Task<bool> RestoreAsync(BackupEntry entry, CancellationToken ct) =>
        RestoreEntryAsync(entry, ct, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private async Task<bool> RestoreEntryAsync(
        BackupEntry? entry,
        CancellationToken ct,
        int depth,
        HashSet<string> visited)
    {
        try
        {
            if (entry is null || depth > MaximumBundleDepth || visited.Count >= MaximumBundleEntries * 2 ||
                !IsEntryDescriptorSafe(entry))
            {
                return false;
            }

            var identity = $"{entry.Kind}\0{Path.GetFullPath(entry.Location)}";
            if (!visited.Add(identity)) return false;
            if (!await VerifyContentAsync(entry, ct).ConfigureAwait(false)) return false;
            return entry.Kind switch
            {
                BackupKind.Registry => await RestoreRegistryAsync(entry, ct).ConfigureAwait(false),
                BackupKind.File => RestoreFile(entry),
                BackupKind.CommandState => await RestoreCommandStateAsync(entry, ct).ConfigureAwait(false),
                BackupKind.ServiceState => await RestoreServicesAsync(entry, ct).ConfigureAwait(false),
                BackupKind.Driver => await RestoreDriverAsync(entry, ct).ConfigureAwait(false),
                BackupKind.Value => await RestoreValueAsync(entry, ct).ConfigureAwait(false),
                BackupKind.Bundle => await RestoreBundleAsync(entry, ct, depth, visited).ConfigureAwait(false),
                _ => false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> RestoreRegistryAsync(BackupEntry entry, CancellationToken ct)
    {
        var result = await _commands.RunAsync("reg.exe", ["import", entry.Location], TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);
        return result.Success;
    }

    private static bool RestoreFile(BackupEntry entry)
    {
        if (!entry.Metadata.TryGetValue("originalPath", out var destination) || string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || HasReparsePointInExistingPath(parent) ||
            File.Exists(destination) && IsReparsePoint(destination))
        {
            return false;
        }

        Directory.CreateDirectory(parent);
        File.Copy(entry.Location, destination, true);
        return true;
    }

    private async Task<bool> RestoreCommandStateAsync(BackupEntry entry, CancellationToken ct)
    {
        if (!TryGetRestoreCommand(entry, out var executable, out var arguments)) return false;
        for (var index = 0; index < arguments.Count; index++)
        {
            arguments[index] = arguments[index].Replace("{backup}", entry.Location, StringComparison.Ordinal);
        }

        var result = await _commands.RunAsync(executable, arguments, TimeSpan.FromMinutes(10), ct)
            .ConfigureAwait(false);
        return result.Success;
    }

    private async Task<bool> RestoreServicesAsync(BackupEntry entry, CancellationToken ct)
    {
        if (!IsBoundedJsonFile(entry.Location)) return false;
        var snapshots = JsonSerializer.Deserialize<List<ServiceSnapshot>>(
            await File.ReadAllTextAsync(entry.Location, ct).ConfigureAwait(false), JsonOptions) ?? [];
        if (snapshots.Count == 0 || snapshots.Count > 128 ||
            snapshots.Any(service => !IsSafeServiceName(service.Name))) return false;

        var success = true;
        foreach (var service in snapshots)
        {
            var start = service.StartMode.ToLowerInvariant() switch
            {
                "auto" or "automatic" => service.DelayedAutoStart ? "delayed-auto" : "auto",
                "disabled" => "disabled",
                _ => "demand"
            };
            var config = await _commands.RunAsync(
                "sc.exe",
                ["config", service.Name, "start=", start],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false);
            success &= config.Success;

            var desiredCommand = service.State.Equals("Running", StringComparison.OrdinalIgnoreCase) ? "start" : "stop";
            var state = await _commands.RunAsync(
                "sc.exe",
                [desiredCommand, service.Name],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false);
            success &= state.Success || state.ExitCode is 1056 or 1062;
        }

        return success;
    }

    private async Task<bool> RestoreDriverAsync(BackupEntry entry, CancellationToken ct)
    {
        var infFiles = EnumerateTrustedFiles(entry.Location)
            .Where(path => path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            .Where(IsTrustedBackupPath)
            .Take(1_025)
            .ToArray();
        if (infFiles.Length is 0 or > 1_024) return false;

        var success = true;
        foreach (var inf in infFiles)
        {
            var result = await _commands.RunAsync(
                "pnputil.exe",
                ["/add-driver", inf, "/install"],
                TimeSpan.FromMinutes(10),
                ct).ConfigureAwait(false);
            success &= result.Success;
        }
        return success;
    }

    private async Task<bool> RestoreValueAsync(BackupEntry entry, CancellationToken ct)
    {
        if (!IsBoundedJsonFile(entry.Location)) return false;
        if (entry.Metadata.TryGetValue("restoreHandler", out var handlerId))
        {
            return _restoreHandlers.TryGetValue(handlerId, out var handler) &&
                   await handler(entry, ct).ConfigureAwait(false);
        }
        if (!TryGetRestoreCommand(entry, out var executable, out var arguments)) return false;
        var value = await File.ReadAllTextAsync(entry.Location, ct).ConfigureAwait(false);
        for (var index = 0; index < arguments.Count; index++)
        {
            arguments[index] = arguments[index]
                .Replace("{valueFile}", entry.Location, StringComparison.Ordinal)
                .Replace("{valueJson}", value, StringComparison.Ordinal);
        }

        var result = await _commands.RunAsync(executable, arguments, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
        return result.Success;
    }

    private async Task<bool> RestoreBundleAsync(
        BackupEntry entry,
        CancellationToken ct,
        int depth,
        HashSet<string> visited)
    {
        if (!IsBoundedJsonFile(entry.Location)) return false;
        var children = JsonSerializer.Deserialize<List<BackupEntry>>(
            await File.ReadAllTextAsync(entry.Location, ct).ConfigureAwait(false), JsonOptions) ?? [];
        if (children.Count == 0 || children.Count > MaximumBundleEntries) return false;

        var success = true;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            success &= await RestoreEntryAsync(children[index], ct, depth + 1, visited).ConfigureAwait(false);
        }
        return success;
    }

    private async Task<BackupEntry> CreateFileEntryAsync(
        BackupKind kind,
        string label,
        string location,
        Dictionary<string, string> metadata,
        CancellationToken ct)
    {
        FlushBackupContentToDisk(location, isDirectory: false, ct);
        return new BackupEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Label = label,
            Location = Path.GetFullPath(location),
            ContentHash = await HashFileAsync(location, ct).ConfigureAwait(false),
            Metadata = metadata
        };
    }

    private async Task<bool> VerifyContentAsync(BackupEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.ContentHash) || entry.ContentHash.Length != 64 ||
            !IsTrustedBackupPath(entry.Location))
        {
            return false;
        }

        var isDirectory = entry.Kind == BackupKind.Driver ||
                          entry.Metadata.TryGetValue("contentType", out var contentType) &&
                          contentType.Equals("directory", StringComparison.OrdinalIgnoreCase);
        var current = isDirectory
            ? Directory.Exists(entry.Location) ? await HashDirectoryAsync(entry.Location, ct).ConfigureAwait(false) : string.Empty
            : File.Exists(entry.Location) ? await HashFileAsync(entry.Location, ct).ConfigureAwait(false) : string.Empty;
        return string.Equals(current, entry.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRestoreCommand(BackupEntry entry, out string executable, out List<string> arguments)
    {
        executable = string.Empty;
        arguments = [];
        if (!entry.Metadata.TryGetValue("restoreExecutable", out var storedExecutable) ||
            string.IsNullOrWhiteSpace(storedExecutable) ||
            !entry.Metadata.TryGetValue("restoreArguments", out var serializedArguments) ||
            serializedArguments is null)
        {
            return false;
        }

        executable = storedExecutable;

        try
        {
            if (serializedArguments.Length > 256 * 1024) return false;
            arguments = JsonSerializer.Deserialize<List<string>>(serializedArguments, JsonOptions) ?? [];
            return IsAllowedRestoreCommand(executable, arguments);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string PrepareDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!IsUnderRoot(fullPath, _sessionsRoot) || HasReparsePointInExistingPath(fullPath))
        {
            throw new InvalidOperationException("The backup directory is outside the trusted session root.");
        }
        Directory.CreateDirectory(fullPath);
        if (HasReparsePointInExistingPath(fullPath)) throw new InvalidOperationException("Reparse points are not allowed in backup paths.");
        return fullPath;
    }

    private bool IsTrustedBackupPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return IsUnderRoot(fullPath, _sessionsRoot) && !HasReparsePointInExistingPath(fullPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUnderRoot(string candidate, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointInExistingPath(string path)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
        return false;
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static Dictionary<string, string> RestoreMetadata(string executable, IReadOnlyList<string> arguments) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["restoreExecutable"] = executable,
            ["restoreArguments"] = JsonSerializer.Serialize(arguments)
        };

    private static bool IsAllowedRestoreCommand(string executable, IReadOnlyList<string> arguments) =>
        AllowedRestoreExecutables.Contains(executable) &&
        string.Equals(Path.GetFileName(executable), executable, StringComparison.OrdinalIgnoreCase) &&
        arguments.Count <= 64 &&
        arguments.All(argument => argument is not null && argument.Length <= 32_768 && argument.IndexOf('\0') < 0);

    private static bool IsEntryDescriptorSafe(BackupEntry entry) =>
        Enum.IsDefined(entry.Kind) &&
        !string.IsNullOrWhiteSpace(entry.Id) && entry.Id.Length <= 128 && entry.Id.IndexOf('\0') < 0 &&
        !string.IsNullOrWhiteSpace(entry.Label) && entry.Label.Length <= 4_096 && entry.Label.IndexOf('\0') < 0 &&
        !string.IsNullOrWhiteSpace(entry.Location) && entry.Location.Length <= 32_767 && entry.Location.IndexOf('\0') < 0 &&
        entry.ContentHash is { Length: 64 } && entry.ContentHash.All(Uri.IsHexDigit) &&
        entry.Metadata is { Count: <= 64 } && entry.Metadata.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && pair.Key.Length <= 256 && pair.Key.IndexOf('\0') < 0 &&
            pair.Value is not null && pair.Value.Length <= 256 * 1024 && pair.Value.IndexOf('\0') < 0);

    private static bool IsBoundedJsonFile(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length is > 0 and <= MaximumJsonBackupBytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static void FlushBackupContentToDisk(
        string path,
        bool isDirectory,
        CancellationToken ct)
    {
        IEnumerable<string> files = isDirectory
            ? EnumerateTrustedFiles(path)
            : new[] { Path.GetFullPath(path) };
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                options: FileOptions.WriteThrough);
            stream.Flush(flushToDisk: true);
        }
    }

    private static async Task<bool> CopyFileBoundedAsync(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken ct)
    {
        var buffer = new byte[81_920];
        var completed = false;
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;
                copied = checked(copied + read);
                if (copied > maximumBytes) return false;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
            completed = true;
            return true;
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (!completed)
            {
                try
                {
                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                }
                catch
                {
                    // An incomplete copy is never returned or trusted as a backup.
                }
            }
        }
    }

    private static async Task<string> HashDirectoryAsync(string directory, CancellationToken ct)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var file in EnumerateTrustedFiles(directory)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (IsReparsePoint(file)) throw new InvalidOperationException("Reparse points are not allowed in driver backups.");
            var length = new FileInfo(file).Length;
            totalBytes = checked(totalBytes + length);
            fileCount++;
            if (totalBytes > MaximumDirectoryBackupBytes || fileCount > MaximumBackupEntries)
            {
                throw new InvalidOperationException("The backup directory exceeds the safe hashing limit.");
            }

            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            aggregate.AppendData(Convert.FromHexString(await HashFileAsync(file, ct).ConfigureAwait(false)));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private static IEnumerable<string> EnumerateTrustedFiles(string root)
    {
        var pending = new Stack<string>();
        var entryCount = 0;
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                entryCount++;
                if (entryCount > MaximumBackupEntries)
                {
                    throw new InvalidOperationException("The backup directory contains too many entries.");
                }

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException("Reparse points are not allowed in backup content.");
                }
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "backup";
        return safe.Length > 55 ? safe[..55] : safe;
    }

    private static bool IsSafeServiceName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    [GeneratedRegex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublishedInfPattern();

    [GeneratedRegex(@"^HK(?:LM|CU|CR|U|CC)\\[^\r\n\0""]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryKeyPattern();

    private sealed class ServiceSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string StartMode { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public bool DelayedAutoStart { get; init; }
    }
}
