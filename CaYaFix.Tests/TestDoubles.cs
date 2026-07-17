// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Concurrent;
using CaYaFix.Core;

namespace CaYaFix.Tests;

internal sealed class RecordingCommandRunner : ICommandRunner
{
    public ConcurrentQueue<CommandCall> Calls { get; } = new();
    public string StandardOutput { get; set; } = "captured-state";
    public int ExitCode { get; set; }

    public Task<CmdResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue(new CommandCall(executable, arguments.ToArray()));
        return Task.FromResult(new CmdResult(ExitCode, StandardOutput, string.Empty, TimeSpan.FromMilliseconds(1)));
    }

    public Task<T?> RunPsJsonAsync<T>(string psCommand, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue(new CommandCall("powershell-json", [psCommand]));
        return Task.FromResult(default(T));
    }
}

internal sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments);

internal sealed class RecordingSessionManager : ISessionManager
{
    public int SaveCount { get; private set; }
    public List<IReadOnlyList<(bool Applied, bool Verified, string Result)>> SavedActionStates { get; } = [];

    public Task<SessionManifest> CreateAsync(bool dryRun, CancellationToken ct) =>
        Task.FromResult(NewSession(dryRun));

    public Task SaveAsync(SessionManifest manifest, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SaveCount++;
        SavedActionStates.Add(manifest.Actions
            .Select(action => (action.Applied, action.Verified, action.ResultMessageKey))
            .ToArray());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionManifest>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionManifest>>([]);

    public Task<SessionManifest?> GetPendingAsync(CancellationToken ct) => Task.FromResult<SessionManifest?>(null);
    public Task<bool> UndoActionAsync(SessionManifest session, int actionIndex, IBackupService backups, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> UndoSessionAsync(SessionManifest session, IBackupService backups, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> DismissActionAsync(SessionManifest session, int actionIndex, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> DismissSessionRecoveryAsync(SessionManifest session, CancellationToken ct) => Task.FromResult(false);

    public static SessionManifest NewSession(bool dryRun = false) => new()
    {
        Id = "test-session",
        DirectoryPath = Path.Combine(Path.GetTempPath(), "CaYaFix.Tests", Guid.NewGuid().ToString("N")),
        DryRun = dryRun
    };
}

internal sealed class StubBackupService : IBackupService
{
    public bool RestoreResult { get; init; }
    public int RestoreCount { get; private set; }

    public Task<BackupEntry?> CaptureRegistryAsync(string key, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureFileAsync(string path, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureCommandStateAsync(string label, string executable, IReadOnlyList<string> captureArguments, string restoreExecutable, IReadOnlyList<string> restoreArguments, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureServicesAsync(IEnumerable<string> serviceNames, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureDriverAsync(string publishedInfName, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureValueAsync(string label, object? value, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureExistingCommandStateAsync(string label, string path, string restoreExecutable, IReadOnlyList<string> restoreArguments, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureBundleAsync(string label, IReadOnlyList<BackupEntry> entries, string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<bool> RestoreAsync(BackupEntry entry, CancellationToken ct)
    {
        RestoreCount++;
        return Task.FromResult(RestoreResult);
    }
}

internal sealed class StubFixAction : FixAction
{
    private readonly List<string> _events;
    private readonly bool _backupSucceeds;
    private readonly bool _applySucceeds;
    private readonly bool _verifySucceeds;
    private readonly bool _cancelOnApply;
    private readonly bool _cancelOnBackup;
    private readonly Action? _beforeApply;

    public StubFixAction(
        string id,
        RiskTier tier,
        List<string> events,
        bool backupSucceeds = true,
        bool applySucceeds = true,
        bool verifySucceeds = true,
        bool requiresReboot = false,
        bool cancelOnApply = false,
        bool cancelOnBackup = false,
        IReadOnlyList<string>? previewSteps = null,
        Action? beforeApply = null)
    {
        Id = id;
        Tier = tier;
        _events = events;
        _backupSucceeds = backupSucceeds;
        _applySucceeds = applySucceeds;
        _verifySucceeds = verifySucceeds;
        _cancelOnApply = cancelOnApply;
        _cancelOnBackup = cancelOnBackup;
        _beforeApply = beforeApply;
        PreviewSteps = previewSteps ?? [];
        RequiresReboot = requiresReboot;
    }

    public override string Id { get; }
    public override string TitleKey => Id;
    public override string ModuleId => "test";
    public override RiskTier Tier { get; }
    public override bool RequiresReboot { get; }
    public override IReadOnlyList<string> PreviewSteps { get; }

    public override Task<BackupEntry?> BackupAsync(FixContext context, CancellationToken ct)
    {
        _events.Add($"{Id}:backup");
        if (_cancelOnBackup) throw new OperationCanceledException();
        return Task.FromResult(_backupSucceeds
            ? new BackupEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = BackupKind.Value,
                Label = Id,
                Location = Path.Combine(context.Session.DirectoryPath, Id),
                ContentHash = new string('0', 64)
            }
            : null);
    }

    public override Task<FixResult> ApplyAsync(FixContext context, CancellationToken ct)
    {
        _beforeApply?.Invoke();
        _events.Add($"{Id}:apply");
        if (_cancelOnApply) throw new OperationCanceledException();
        return Task.FromResult(_applySucceeds ? FixResult.Ok("ok") : FixResult.Fail("failed"));
    }

    public override Task<bool> VerifyAsync(FixContext context, CancellationToken ct)
    {
        _events.Add($"{Id}:verify");
        return Task.FromResult(_verifySucceeds);
    }
}

internal sealed class StubDiagnosticCheck : DiagnosticCheck
{
    private readonly bool _returnsFinding;

    public StubDiagnosticCheck(string id, bool returnsFinding)
    {
        Id = id;
        _returnsFinding = returnsFinding;
    }

    public override string Id { get; }
    public override string TitleKey => Id;
    public override string ModuleId => "test";

    public override Task<Finding?> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Finding?>(_returnsFinding
            ? new Finding
            {
                CheckId = Id,
                ModuleId = ModuleId,
                Severity = Severity.Warning,
                MessageKey = "still-present"
            }
            : null);
    }
}
