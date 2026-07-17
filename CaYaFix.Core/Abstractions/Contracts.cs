// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Core;

public interface ICommandRunner
{
    Task<CmdResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<T?> RunPsJsonAsync<T>(string psCommand, CancellationToken cancellationToken);
}

public interface IConsoleSink
{
    void Write(ConsoleLine line);
}

public interface ITextProvider
{
    string Get(string key, params object[] arguments);
}

public interface IBackupService
{
    Task<BackupEntry?> CaptureRegistryAsync(string key, string directory, CancellationToken ct);
    Task<BackupEntry?> CaptureFileAsync(string path, string directory, CancellationToken ct);
    Task<BackupEntry?> CaptureCommandStateAsync(
        string label,
        string executable,
        IReadOnlyList<string> captureArguments,
        string restoreExecutable,
        IReadOnlyList<string> restoreArguments,
        string directory,
        CancellationToken ct);
    Task<BackupEntry?> CaptureServicesAsync(IEnumerable<string> serviceNames, string directory, CancellationToken ct);
    Task<BackupEntry?> CaptureDriverAsync(string publishedInfName, string directory, CancellationToken ct);
    Task<BackupEntry?> CaptureValueAsync(string label, object? value, string directory, CancellationToken ct);
    Task<BackupEntry?> CaptureExistingCommandStateAsync(
        string label,
        string path,
        string restoreExecutable,
        IReadOnlyList<string> restoreArguments,
        CancellationToken ct);
    Task<BackupEntry?> CaptureBundleAsync(
        string label,
        IReadOnlyList<BackupEntry> entries,
        string directory,
        CancellationToken ct);
    Task<bool> RestoreAsync(BackupEntry entry, CancellationToken ct);
}

public interface IIntegrityService
{
    string Sign(string purpose, ReadOnlySpan<byte> content);
    bool Verify(string purpose, ReadOnlySpan<byte> content, string signature);
}

public interface ISessionManager
{
    Task<SessionManifest> CreateAsync(bool dryRun, CancellationToken ct);
    Task SaveAsync(SessionManifest manifest, CancellationToken ct);
    Task<IReadOnlyList<SessionManifest>> ListAsync(CancellationToken ct);
    Task<SessionManifest?> GetPendingAsync(CancellationToken ct);
    Task<bool> UndoActionAsync(SessionManifest session, int actionIndex, IBackupService backups, CancellationToken ct);
    Task<bool> UndoSessionAsync(SessionManifest session, IBackupService backups, CancellationToken ct);
    Task<bool> DismissActionAsync(SessionManifest session, int actionIndex, CancellationToken ct);
    Task<bool> DismissSessionRecoveryAsync(SessionManifest session, CancellationToken ct);
}

public abstract class DiagnosticCheck
{
    public abstract string Id { get; }
    public abstract string TitleKey { get; }
    public abstract string ModuleId { get; }
    public virtual bool IsQuickCheck => true;
    public virtual bool SupportsPostRepairVerification => true;
    public abstract Task<Finding?> RunAsync(DiagnosticContext context, CancellationToken ct);
}

public abstract class FixAction
{
    public abstract string Id { get; }
    public abstract string TitleKey { get; }
    public abstract string ModuleId { get; }
    public abstract RiskTier Tier { get; }
    public virtual bool RequiresReboot => false;
    public virtual bool RequiresTargetParameter => false;
    public virtual IReadOnlyList<string> PreviewSteps => [];
    public abstract Task<BackupEntry?> BackupAsync(FixContext context, CancellationToken ct);
    public abstract Task<FixResult> ApplyAsync(FixContext context, CancellationToken ct);
    public abstract Task<bool> VerifyAsync(FixContext context, CancellationToken ct);
    public virtual Task<bool> UndoAsync(FixContext context, BackupEntry backup, CancellationToken ct) =>
        context.Backups.RestoreAsync(backup, ct);
}

public abstract class LiveTest
{
    public abstract string Id { get; }
    public abstract string TitleKey { get; }
    public abstract string ModuleId { get; }
    public abstract IAsyncEnumerable<TestProgress> RunAsync(
        DiagnosticContext context,
        CancellationToken ct);
}

public interface IModuleDefinition
{
    ModuleInfo Info { get; }
    IReadOnlyList<DiagnosticCheck> Checks { get; }
    IReadOnlyList<FixAction> Fixes { get; }
    IReadOnlyList<LiveTest> LiveTests { get; }
    IReadOnlyList<Playbook> Playbooks { get; }
}

public sealed class PassthroughTextProvider : ITextProvider
{
    public string Get(string key, params object[] arguments) =>
        arguments.Length == 0 ? key : string.Format(key, arguments);
}

public sealed class EventConsoleSink : IConsoleSink
{
    private readonly object _gate = new();
    private readonly Queue<ConsoleLine> _history = new();
    private readonly int _capacity;

    public EventConsoleSink(int capacity = 2_000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public event EventHandler<ConsoleLine>? LineWritten;

    public IReadOnlyList<ConsoleLine> Snapshot()
    {
        lock (_gate)
        {
            return _history.ToArray();
        }
    }

    public void Write(ConsoleLine line)
    {
        lock (_gate)
        {
            _history.Enqueue(line);
            while (_history.Count > _capacity)
            {
                _history.Dequeue();
            }
        }

        LineWritten?.Invoke(this, line);
    }
}
