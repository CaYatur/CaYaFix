// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text;
using CaYaFix.Core;
using CaYaFix.Modules;

// Dry-run every non-target repair through FixEngine. Never mutates Windows.

var modules = ModuleCatalog.CreateAll();
var console = new CollectingConsole();
var sessions = new EphemeralSessionManager();
var backups = new BlockingBackupService();
var commands = new NoOpCommandRunner(console);
var text = new PassthroughTextProvider();
var engine = new FixEngine(sessions);

var session = await sessions.CreateAsync(dryRun: true, CancellationToken.None).ConfigureAwait(false);
var requests = new List<FixRequest>();
var skippedTarget = 0;

foreach (var module in modules)
{
    foreach (var fix in module.Fixes)
    {
        if (fix.RequiresTargetParameter)
        {
            skippedTarget++;
            continue;
        }

        var finding = new Finding
        {
            CheckId = $"dryrun.{fix.Id}",
            ModuleId = module.Info.Id,
            Severity = Severity.Info,
            MessageKey = "Finding_GuidedSymptom",
            MessageArguments = ["dry-run", fix.Id],
            RecommendedFixIds = [fix.Id]
        };
        requests.Add(new FixRequest(finding, fix));
    }
}

var context = new FixContext
{
    Commands = commands,
    Backups = backups,
    Console = console,
    Text = text,
    Session = session,
    DryRun = true,
    ForceModeConfirmed = true,
    RestorePointAvailable = true,
    AllowBackuplessAggressive = false,
    Thresholds = new Thresholds()
};

var results = await engine.RunAsync(requests, context, null, CancellationToken.None).ConfigureAwait(false);
var ok = results.Count(r => r.MessageKey == "FixResult_DryRun");
var other = results.Where(r => r.MessageKey != "FixResult_DryRun").ToArray();

Console.WriteLine($"Modules: {modules.Count}");
Console.WriteLine($"Checks: {modules.Sum(m => m.Checks.Count)}");
Console.WriteLine($"Fixes: {modules.Sum(m => m.Fixes.Count)}");
Console.WriteLine($"Dry-run requests: {requests.Count}");
Console.WriteLine($"Skipped target-required: {skippedTarget}");
Console.WriteLine($"DryRun results OK: {ok}");
Console.WriteLine($"Unexpected results: {other.Length}");
foreach (var item in other.Take(20))
{
    Console.WriteLine($"  UNEXPECTED {item.FixId}: {item.MessageKey}");
}

// Preview step coverage
var missingPreview = modules
    .SelectMany(m => m.Fixes)
    .Where(f => f.PreviewSteps.Count == 0)
    .Select(f => f.Id)
    .ToArray();
Console.WriteLine($"Missing preview steps: {missingPreview.Length}");
foreach (var id in missingPreview)
{
    Console.WriteLine($"  NO_PREVIEW {id}");
}

var planPath = Path.Combine(Path.GetTempPath(), $"cayafix-dryrun-plans-{DateTime.Now:yyyyMMddHHmmss}.md");
var sb = new StringBuilder();
sb.AppendLine("# CaYaFix dry-run plans (harmless)");
foreach (var module in modules.OrderBy(m => m.Info.Priority))
{
    sb.AppendLine();
    sb.AppendLine($"## {module.Info.Id}");
    foreach (var fix in module.Fixes.OrderBy(f => f.Tier).ThenBy(f => f.Id))
    {
        sb.AppendLine($"### {fix.Id} · Tier={fix.Tier} · Target={fix.RequiresTargetParameter} · Reboot={fix.RequiresReboot}");
        foreach (var step in fix.PreviewSteps)
        {
            sb.AppendLine($"- {step}");
        }
    }
}
await File.WriteAllTextAsync(planPath, sb.ToString()).ConfigureAwait(false);
Console.WriteLine($"Plan book: {planPath}");

if (other.Length > 0 || missingPreview.Length > 0)
{
    return 1;
}

Console.WriteLine("HARMLESS_DRYRUN_OK");
return 0;

sealed class CollectingConsole : IConsoleSink
{
    public List<ConsoleLine> Lines { get; } = [];
    public void Write(ConsoleLine line) => Lines.Add(line);
}

sealed class EphemeralSessionManager : ISessionManager
{
    private SessionManifest? _session;

    public Task<SessionManifest> CreateAsync(bool dryRun, CancellationToken ct)
    {
        _session = new SessionManifest
        {
            Id = Guid.NewGuid().ToString("N"),
            DirectoryPath = Path.Combine(Path.GetTempPath(), "cayafix-dryrun-" + Guid.NewGuid().ToString("N")),
            DryRun = dryRun
        };
        Directory.CreateDirectory(_session.DirectoryPath);
        return Task.FromResult(_session);
    }

    public Task SaveAsync(SessionManifest session, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<SessionManifest>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionManifest>>([]);
    public Task<SessionManifest?> GetPendingAsync(CancellationToken ct) =>
        Task.FromResult<SessionManifest?>(null);
    public Task<bool> UndoActionAsync(SessionManifest session, int actionIndex, IBackupService backups, CancellationToken ct) =>
        Task.FromResult(false);
    public Task<bool> UndoSessionAsync(SessionManifest session, IBackupService backups, CancellationToken ct) =>
        Task.FromResult(false);
    public Task<bool> DismissActionAsync(SessionManifest session, int actionIndex, CancellationToken ct) =>
        Task.FromResult(false);
    public Task<bool> DismissSessionRecoveryAsync(SessionManifest session, CancellationToken ct) =>
        Task.FromResult(false);
}

sealed class BlockingBackupService : IBackupService
{
    public Task<BackupEntry?> CaptureRegistryAsync(string key, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureFileAsync(string path, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureCommandStateAsync(
        string label, string executable, IReadOnlyList<string> captureArguments,
        string restoreExecutable, IReadOnlyList<string> restoreArguments,
        string directory, CancellationToken ct) => Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureServicesAsync(IEnumerable<string> serviceNames, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureDriverAsync(string publishedInfName, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureValueAsync(string label, object? value, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureExistingCommandStateAsync(
        string label, string path, string restoreExecutable, IReadOnlyList<string> restoreArguments, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<BackupEntry?> CaptureBundleAsync(string label, IReadOnlyList<BackupEntry> entries, string directory, CancellationToken ct) =>
        Task.FromResult<BackupEntry?>(null);
    public Task<bool> RestoreAsync(BackupEntry backup, CancellationToken ct) => Task.FromResult(false);
}

sealed class NoOpCommandRunner : ICommandRunner
{
    private readonly IConsoleSink _console;
    public NoOpCommandRunner(IConsoleSink console) => _console = console;

    public Task<CmdResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Dry-run engine should never reach here; fail loudly if it does.
        _console.Write(new ConsoleLine(DateTimeOffset.Now, "ERR", $"Unexpected command: {executable}"));
        return Task.FromResult(new CmdResult(-99, "", "Dry-run should not execute commands", TimeSpan.Zero));
    }

    public Task<T?> RunPsJsonAsync<T>(string psCommand, CancellationToken cancellationToken) =>
        Task.FromResult<T?>(default);
}
