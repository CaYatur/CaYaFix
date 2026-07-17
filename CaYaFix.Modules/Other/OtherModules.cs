// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;
using static CaYaFix.Modules.Other.OtherModuleFunctions;

namespace CaYaFix.Modules.Other;

public abstract class WindowsModuleBase : IModuleDefinition
{
    protected WindowsModuleBase(
        ModuleInfo info,
        IReadOnlyList<DiagnosticCheck> checks,
        IReadOnlyList<FixAction> fixes,
        IReadOnlyList<Playbook> playbooks,
        IReadOnlyList<LiveTest>? liveTests = null)
    {
        Info = info;
        Checks = checks;
        Fixes = fixes;
        Playbooks = playbooks;
        LiveTests = liveTests ?? [];
    }

    public ModuleInfo Info { get; }
    public IReadOnlyList<DiagnosticCheck> Checks { get; }
    public IReadOnlyList<FixAction> Fixes { get; }
    public IReadOnlyList<LiveTest> LiveTests { get; }
    public IReadOnlyList<Playbook> Playbooks { get; }
}

public sealed class WindowsUpdateModule : WindowsModuleBase
{
    private const string Id = "windows-update";
    private static readonly string[] Services = ["wuauserv", "BITS", "cryptsvc"];

    public WindowsUpdateModule() : base(
        new ModuleInfo(Id, "Module_Update_Name", "Module_Update_Description", "update.svg", 2),
        CreateChecks(), CreateFixes(),
        [new Playbook("update.failed", Id, "Symptom_Update_Failed", ["update.services", "update.errors", "update.pending"], ["update.restart-services", "update.reset-cache"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("update.services", "Check_Update_Services", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('wuauserv','BITS','cryptsvc')} | Select-Object Name,State,StartMode", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var unhealthy = rows.Length != Services.Length || rows.Any(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ModuleHelpers.GetString(row, "Name"), "cryptsvc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("update.services", Id, Severity.Warning, "Finding_Update_ServiceIssue", Join(rows), "update.restart-services")
                : null;
        }),
        new DelegateDiagnosticCheck("update.errors", "Check_Update_Errors", Id, async (context, ct) =>
        {
            const string script = "$since=(Get-Date).AddDays(-7); Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-WindowsUpdateClient';StartTime=$since;Level=2} -MaxEvents 10 -ErrorAction SilentlyContinue | Select-Object TimeCreated,Id,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var errors = ModuleHelpers.Array(root).ToArray();
            return errors.Length > 0 ? ModuleHelpers.Finding("update.errors", Id, Severity.Warning, "Finding_Update_RecentErrors", ExplainUpdateErrors(Join(errors), context.Text), "update.reset-cache") : null;
        }, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("update.pending", "Check_Update_PendingReboot", Id, async (context, ct) =>
        {
            const string script = "$pending=(Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending') -or (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired'); [pscustomobject]@{Pending=$pending}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var pending = ModuleHelpers.Array(root).Any(row => Bool(row, "Pending"));
            return pending ? ModuleHelpers.Finding("update.pending", Id, Severity.Info, "Finding_Update_RebootPending", string.Empty) : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("update.restart-services", "Fix_Update_RestartServices", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(Services, ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Set-Service BITS -StartupType Manual; Set-Service cryptsvc -StartupType Automatic; Set-Service wuauserv -StartupType Manual; Start-Service BITS; Start-Service cryptsvc; Start-Service wuauserv"], TimeSpan.FromMinutes(4))], ct),
            VerifyUpdateServicesAsync),
        new DelegateFixAction("update.reset-cache", "Fix_Update_ResetCache", Id, RiskTier.Moderate,
            BackupUpdateCacheAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", ResetUpdateCacheScript()], TimeSpan.FromMinutes(10))], ct),
            async (context, ct) => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution")) &&
                                   await VerifyUpdateServicesAsync(context, ct).ConfigureAwait(false)),
        ExpandedRepairHelpers.RestartNamedServices(
            "update.restart-bits", "Fix_Update_RestartBits", Id,
            ["BITS", "wuauserv"], "BITS"),
        ExpandedRepairHelpers.RestartNamedServices(
            "update.restart-cryptsvc", "Fix_Update_RestartCryptSvc", Id,
            ["cryptsvc", "BITS", "wuauserv"], "cryptsvc"),
        // USO / Update Orchestrator participates in modern Windows Update.
        ExpandedRepairHelpers.RestartNamedServices(
            "update.restart-uso", "Fix_Update_RestartUso", Id,
            ["UsoSvc", "wuauserv", "BITS"], "wuauserv"),
        ExpandedRepairHelpers.TransientCommand(
            "update.clean-download",
            "Fix_Update_CleanDownload",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Service wuauserv,BITS -Force -ErrorAction SilentlyContinue; " +
                        "$dl=Join-Path $env:windir 'SoftwareDistribution\\Download'; " +
                        "if(Test-Path -LiteralPath $dl){ Get-ChildItem -LiteralPath $dl -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; " +
                        "Start-Service cryptsvc,BITS,wuauserv -ErrorAction SilentlyContinue; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ],
            VerifyUpdateServicesAsync),
        ExpandedRepairHelpers.TransientCommand(
            "update.sc-defaults",
            "Fix_Update_ScDefaults",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("sc.exe", ["config", "BITS", "start=", "demand"]),
                new CommandStep("sc.exe", ["config", "wuauserv", "start=", "demand"]),
                new CommandStep("sc.exe", ["config", "cryptsvc", "start=", "auto"]),
                new CommandStep("sc.exe", ["start", "cryptsvc"]) { AcceptedExitCodes = new HashSet<int> { 0, 1056 } },
                new CommandStep("sc.exe", ["start", "BITS"]) { AcceptedExitCodes = new HashSet<int> { 0, 1056 } },
                new CommandStep("sc.exe", ["start", "wuauserv"]) { AcceptedExitCodes = new HashSet<int> { 0, 1056 } }
            ],
            VerifyUpdateServicesAsync)
    ];

    private static async Task<BackupEntry?> BackupUpdateCacheAsync(FixContext context, CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var services = await context.Backups.CaptureServicesAsync(Services, dir, ct).ConfigureAwait(false);
        if (services is null) return null;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sourceOne = Path.Combine(windows, "SoftwareDistribution");
        var sourceTwo = Path.Combine(windows, "System32", "catroot2");
        var cacheBackup = Path.Combine(dir, "WindowsUpdateCache");
        if (!TryMeasureDirectories([sourceOne, sourceTwo], 4L * 1024 * 1024 * 1024, 150_000, out var backupBytes) ||
            !HasFreeSpace(dir, backupBytes + 512L * 1024 * 1024))
        {
            return null;
        }

        Directory.CreateDirectory(cacheBackup);
        var backupOne = Path.Combine(cacheBackup, "SoftwareDistribution.old");
        var backupTwo = Path.Combine(cacheBackup, "catroot2.old");
        CmdResult copy;
        var servicesRestored = false;
        try
        {
            var script = $"$ErrorActionPreference='Stop'; Stop-Service wuauserv,BITS,cryptsvc -Force -ErrorAction SilentlyContinue; if(Test-Path -LiteralPath '{Ps(sourceOne)}'){{Copy-Item -LiteralPath '{Ps(sourceOne)}' -Destination '{Ps(backupOne)}' -Recurse -Force}}; if(Test-Path -LiteralPath '{Ps(sourceTwo)}'){{Copy-Item -LiteralPath '{Ps(sourceTwo)}' -Destination '{Ps(backupTwo)}' -Recurse -Force}}";
            copy = await context.Commands.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromMinutes(15),
                ct).ConfigureAwait(false);
        }
        finally
        {
            servicesRestored = await context.Backups.RestoreAsync(services, CancellationToken.None).ConfigureAwait(false);
        }
        if (!copy.Success || !servicesRestored) return null;

        var restore = await context.Backups.CaptureExistingCommandStateAsync(
            "Windows Update cache",
            cacheBackup,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"$ErrorActionPreference='Stop'; Stop-Service wuauserv,BITS,cryptsvc -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{Ps(sourceOne)}' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{Ps(sourceTwo)}' -Recurse -Force -ErrorAction SilentlyContinue; if(Test-Path -LiteralPath '{Ps(backupOne)}'){{Copy-Item -LiteralPath '{Ps(backupOne)}' -Destination '{Ps(sourceOne)}' -Recurse -Force}}else{{New-Item -ItemType Directory -Path '{Ps(sourceOne)}' -Force|Out-Null}}; if(Test-Path -LiteralPath '{Ps(backupTwo)}'){{Copy-Item -LiteralPath '{Ps(backupTwo)}' -Destination '{Ps(sourceTwo)}' -Recurse -Force}}else{{New-Item -ItemType Directory -Path '{Ps(sourceTwo)}' -Force|Out-Null}}"],
            ct).ConfigureAwait(false);
        if (restore is null) return null;
        return await context.Backups.CaptureBundleAsync("Windows Update", [services, restore], dir, ct).ConfigureAwait(false);
    }

    private static string ResetUpdateCacheScript()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sourceOne = Ps(Path.Combine(windows, "SoftwareDistribution"));
        var sourceTwo = Ps(Path.Combine(windows, "System32", "catroot2"));
        return $"$ErrorActionPreference='Stop'; Stop-Service wuauserv,BITS,cryptsvc -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{sourceOne}' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{sourceTwo}' -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path '{sourceOne}' -Force|Out-Null; New-Item -ItemType Directory -Path '{sourceTwo}' -Force|Out-Null; Start-Service cryptsvc,BITS,wuauserv -ErrorAction Stop";
    }

    private static async Task<bool> VerifyUpdateServicesAsync(FixContext context, CancellationToken ct)
    {
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('wuauserv','BITS','cryptsvc')} | Select-Object Name,State,StartMode",
            ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        return rows.Length == Services.Length && rows.All(row =>
            !string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) &&
            (!string.Equals(ModuleHelpers.GetString(row, "Name"), "cryptsvc", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase)));
    }

    private static string ExplainUpdateErrors(string value, ITextProvider text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x80070002"] = "UpdateError_80070002",
            ["0x8007000E"] = "UpdateError_8007000E",
            ["0x80244022"] = "UpdateError_80244022",
            ["0x800F081F"] = "UpdateError_800F081F"
        };
        foreach (var pair in map)
        {
            if (value.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)) value += $"{Environment.NewLine}{pair.Key}: {text.Get(pair.Value)}";
        }
        return value;
    }

    private static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public sealed class PrinterModule : WindowsModuleBase
{
    private const string Id = "printer";

    public PrinterModule() : base(
        new ModuleInfo(Id, "Module_Printer_Name", "Module_Printer_Description", "printer.svg", 3),
        CreateChecks(), CreateFixes(),
        [new Playbook(
            "printer.stuck",
            Id,
            "Symptom_Printer_Stuck",
            ["printer.spooler", "printer.queues", "printer.offline", "printer.default"],
            ["printer.restart-spooler", "printer.reset-queue"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("printer.spooler", "Check_Printer_Spooler", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-CimInstance Win32_Service -Filter \"Name='Spooler'\" | Select-Object State,StartMode", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var unhealthy = rows.Length != 1 || rows.Any(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("printer.spooler", Id, Severity.Critical, "Finding_Printer_SpoolerStopped", Join(rows), "printer.restart-spooler")
                : null;
        }),
        new DelegateDiagnosticCheck("printer.queues", "Check_Printer_Queues", Id, async (context, ct) =>
        {
            const string script = "Get-Printer -ErrorAction Stop | ForEach-Object {$printer=$_.Name; Get-PrintJob -PrinterName $printer -ErrorAction SilentlyContinue | Where-Object {[string]$_.JobStatus -match 'Error|Blocked|Offline|PaperOut|UserIntervention|Retained|Deleting'} | Select-Object @{n='PrinterName';e={$printer}},ID,JobStatus,SubmittedTime,TotalPages,PagesPrinted}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root).ToArray();
            return bad.Length > 0
                ? ModuleHelpers.Finding("printer.queues", Id, Severity.Warning, "Finding_Printer_QueueProblem", Join(bad), "printer.reset-queue")
                : null;
        }),
        new DelegateDiagnosticCheck("printer.offline", "Check_Printer_Offline", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-Printer -ErrorAction Stop | Where-Object WorkOffline | Select-Object Name,PrinterStatus,DriverName,PortName", ct).ConfigureAwait(false);
            var offline = ModuleHelpers.Array(root).ToArray();
            return offline.Length > 0
                ? ModuleHelpers.Finding("printer.offline", Id, Severity.Info, "Finding_Printer_Offline", Join(offline), "printer.set-online", "printer.restart-spooler")
                : null;
        }),
        new DelegateDiagnosticCheck("printer.default", "Check_Printer_Default", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-Printer -ErrorAction Stop | Select-Object Name,Default,WorkOffline", ct).ConfigureAwait(false);
            var printers = ModuleHelpers.Array(root).ToArray();
            if (printers.Length == 0 || printers.Any(row => Bool(row, "Default"))) return null;

            var preferred = printers.FirstOrDefault(row => !Bool(row, "WorkOffline"));
            if (preferred.ValueKind != JsonValueKind.Object) preferred = printers[0];
            var target = ModuleHelpers.GetString(preferred, "Name");
            var finding = ModuleHelpers.Finding(
                "printer.default",
                Id,
                Severity.Warning,
                "Finding_Printer_DefaultMissing",
                string.IsNullOrWhiteSpace(target) ? Join(printers) : $"Suggested={target}{Environment.NewLine}{Join(printers)}",
                "printer.set-default");
            if (!string.IsNullOrWhiteSpace(target))
            {
                finding.RepairParameters["printer.set-default.target"] = target;
            }

            return finding;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("printer.restart-spooler", "Fix_Printer_RestartSpooler", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["Spooler"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service Spooler -StartupType Automatic; Restart-Service Spooler -Force -ErrorAction Stop"])], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "Spooler", ct)),
        new DelegateFixAction("printer.reset-queue", "Fix_Printer_ResetQueue", Id, RiskTier.Moderate,
            BackupPrinterQueueAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Stop-Service Spooler -Force -ErrorAction SilentlyContinue; $queue=Join-Path $env:windir 'System32\\spool\\PRINTERS'; if(Test-Path -LiteralPath $queue){Get-ChildItem -LiteralPath $queue -File -Force -ErrorAction SilentlyContinue|Remove-Item -Force -ErrorAction Stop}; Start-Service Spooler -ErrorAction Stop"], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync("powershell.exe", ["-NoProfile", "-Command", "if((Get-ChildItem \"$env:windir\\System32\\spool\\PRINTERS\" -Force -ErrorAction SilentlyContinue).Count -eq 0){exit 0}else{exit 1}"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                return result.Success;
            }),
        new DelegateFixAction("printer.set-online", "Fix_Printer_SetOnline", Id, RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "printer-online", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Get-Printer | Where-Object WorkOffline | ForEach-Object { Set-Printer -Name $_.Name -WorkOffline:$false -ErrorAction SilentlyContinue }"], TimeSpan.FromMinutes(3))], ct),
            async (context, ct) =>
            {
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    "Get-Printer -ErrorAction SilentlyContinue | Where-Object WorkOffline | Select-Object Name", ct).ConfigureAwait(false);
                return !ModuleHelpers.Array(root).Any();
            }),
        ExpandedRepairHelpers.RestartNamedServices(
            "printer.restart-print-pipeline", "Fix_Printer_RestartPrintPipeline", Id,
            ["Spooler", "PrintNotify", "PrintWorkflowUserSvc"], "Spooler"),
        ExpandedRepairHelpers.TransientCommand(
            "printer.purge-spool",
            "Fix_Printer_PurgeSpool",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; Stop-Service Spooler -Force -ErrorAction SilentlyContinue; " +
                        "$q=Join-Path $env:windir 'System32\\spool\\PRINTERS'; " +
                        "if(Test-Path -LiteralPath $q){ Get-ChildItem -LiteralPath $q -File -Force -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue }; " +
                        "Start-Service Spooler -ErrorAction SilentlyContinue; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ],
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "Spooler", ct)),
        ExpandedRepairHelpers.TransientCommand(
            "printer.cancel-error-jobs",
            "Fix_Printer_CancelErrorJobs",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Get-Printer -EA SilentlyContinue | ForEach-Object { " +
                        "  Get-PrintJob -PrinterName $_.Name -EA SilentlyContinue | " +
                        "  Where-Object { [string]$_.JobStatus -match 'Error|Blocked|Offline|PaperOut|UserIntervention' } | " +
                        "  ForEach-Object { try{ Remove-PrintJob -PrinterName $_.PrinterName -ID $_.Id -EA SilentlyContinue }catch{} } " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(4))
            ],
            async (_, _) => await Task.FromResult(true)),
        ExpandedRepairHelpers.TransientCommand(
            "printer.sc-auto-spooler",
            "Fix_Printer_ScAutoSpooler",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("sc.exe", ["config", "Spooler", "start=", "auto"]),
                new CommandStep("sc.exe", ["start", "Spooler"]) { AcceptedExitCodes = new HashSet<int> { 0, 1056 } }
            ],
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "Spooler", ct)),
        ExpandedRepairHelpers.TransientCommand(
            "printer.test-spool-folder",
            "Fix_Printer_EnsureSpoolFolder",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Stop'; $q=Join-Path $env:windir 'System32\\spool\\PRINTERS'; " +
                        "if(-not (Test-Path -LiteralPath $q)){ New-Item -ItemType Directory -Path $q -Force | Out-Null }; exit 0"
                    ],
                    TimeSpan.FromMinutes(2))
            ],
            async (_, _) => await Task.FromResult(true)),
        new DelegateFixAction("printer.set-default", "Fix_Printer_SetDefault", Id, RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "printer-default", ct),
            (context, ct) =>
            {
                if (!context.Parameters.TryGetValue("printer.set-default.target", out var name) ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOf('\0') >= 0)
                {
                    return Task.FromResult(FixResult.Fail("FixResult_TargetRequired"));
                }

                var literal = name.Replace("'", "''", StringComparison.Ordinal);
                return ModuleHelpers.RunSequenceAsync(context,
                    [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", $"$printer=Get-Printer -Name '{literal}' -ErrorAction Stop; $printer | Set-Printer -Default"], TimeSpan.FromMinutes(2))], ct);
            },
            async (context, ct) =>
            {
                if (!context.Parameters.TryGetValue("printer.set-default.target", out var name) ||
                    string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                var literal = name.Replace("'", "''", StringComparison.Ordinal);
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    $"Get-Printer -Name '{literal}' -ErrorAction SilentlyContinue | Select-Object Name,Default", ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Bool(row, "Default"));
            },
            requiresTarget: true)
    ];

    private static async Task<BackupEntry?> BackupPrinterQueueAsync(FixContext context, CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var service = await context.Backups.CaptureServicesAsync(["Spooler"], dir, ct).ConfigureAwait(false);
        if (service is null) return null;
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "PRINTERS");
        var destination = Path.Combine(dir, "PrinterQueue");
        if (!TryMeasureDirectories([source], 1024L * 1024 * 1024, 50_000, out var backupBytes) ||
            !HasFreeSpace(dir, backupBytes + 128L * 1024 * 1024))
        {
            return null;
        }

        Directory.CreateDirectory(destination);
        CmdResult copy;
        var serviceRestored = false;
        try
        {
            var script = $"$ErrorActionPreference='Stop'; Stop-Service Spooler -Force -ErrorAction SilentlyContinue; if(Test-Path -LiteralPath '{Ps(source)}'){{Get-ChildItem -LiteralPath '{Ps(source)}' -File -Force -ErrorAction SilentlyContinue|Copy-Item -Destination '{Ps(destination)}' -Force -ErrorAction Stop}}";
            copy = await context.Commands.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromMinutes(5),
                ct).ConfigureAwait(false);
        }
        finally
        {
            serviceRestored = await context.Backups.RestoreAsync(service, CancellationToken.None).ConfigureAwait(false);
        }
        if (!copy.Success || !serviceRestored) return null;

        var restore = await context.Backups.CaptureExistingCommandStateAsync(
            "Printer queue", destination, "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"$ErrorActionPreference='Stop'; Stop-Service Spooler -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Path '{Ps(source)}' -Force|Out-Null; Get-ChildItem -LiteralPath '{Ps(source)}' -File -Force -ErrorAction SilentlyContinue|Remove-Item -Force -ErrorAction Stop; Get-ChildItem -LiteralPath '{Ps(destination)}' -File -Force -ErrorAction SilentlyContinue|Copy-Item -Destination '{Ps(source)}' -Force -ErrorAction Stop"],
            ct).ConfigureAwait(false);
        if (restore is null) return null;
        return await context.Backups.CaptureBundleAsync("Printer queue", [service, restore], dir, ct).ConfigureAwait(false);
    }

    private static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

public sealed class BluetoothModule : WindowsModuleBase
{
    private const string Id = "bluetooth";

    public BluetoothModule() : base(
        new ModuleInfo(Id, "Module_Bluetooth_Name", "Module_Bluetooth_Description", "bluetooth.svg", 4),
        CreateChecks(), CreateFixes(),
        [new Playbook("bluetooth.connect", Id, "Symptom_Bluetooth_Connect",
            ["bluetooth.service", "bluetooth.devices"],
            ["bluetooth.restart-service", "bluetooth.restart-stack", "bluetooth.scan-devices", "bluetooth.enable-adapters", "bluetooth.restart-radios", "bluetooth.restart-device"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("bluetooth.service", "Check_Bluetooth_Service", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-CimInstance Win32_Service -Filter \"Name='bthserv'\" | Select-Object State,StartMode", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var unhealthy = rows.Length != 1 || rows.Any(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("bluetooth.service", Id, Severity.Warning, "Finding_Bluetooth_ServiceDisabled", Join(rows),
                    "bluetooth.restart-service", "bluetooth.restart-stack")
                : null;
        }),
        new DelegateDiagnosticCheck("bluetooth.devices", "Check_Bluetooth_Devices", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-CimInstance Win32_PnPEntity | Where-Object {$_.PNPClass -eq 'Bluetooth' -and $_.ConfigManagerErrorCode -ne 0} | Select-Object Name,DeviceID,ConfigManagerErrorCode", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            if (rows.Length == 0) return null;
            var finding = ModuleHelpers.Finding("bluetooth.devices", Id, Severity.Warning, "Finding_Bluetooth_DeviceError", Join(rows),
                "bluetooth.scan-devices", "bluetooth.enable-adapters", "bluetooth.restart-device", "bluetooth.restart-radios");
            var target = ModuleHelpers.GetString(rows[0], "DeviceID");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["bluetooth.restart-device.target"] = target;
            return finding;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("bluetooth.restart-service", "Fix_Bluetooth_RestartService", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["bthserv"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service bthserv -StartupType Manual; Start-Service bthserv -ErrorAction Stop"])], ct),
            async (context, ct) => (await context.Commands.RunAsync("sc.exe", ["query", "bthserv"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success),
        // Support services used by classic Bluetooth audio / AVRCP stacks.
        ExpandedRepairHelpers.RestartNamedServices(
            "bluetooth.restart-stack", "Fix_Bluetooth_RestartStack", Id,
            ["bthserv", "BthAvctpSvc", "BTAGService", "DeviceAssociationService"], "bthserv"),
        ExpandedRepairHelpers.PnpScanDevices("bluetooth.scan-devices", "Fix_Bluetooth_ScanDevices", Id),
        ExpandedRepairHelpers.EnableDisabledPnp(
            "bluetooth.enable-adapters", "Fix_Bluetooth_EnableAdapters", Id,
            "$_.Class -eq 'Bluetooth' -or $_.PNPClass -eq 'Bluetooth'", "Bluetooth|Radio"),
        ExpandedRepairHelpers.RestartPnpByClass(
            "bluetooth.restart-radios", "Fix_Bluetooth_RestartRadios", Id, "Bluetooth", maxDevices: 6),
        ExpandedRepairHelpers.TransientCommand(
            "bluetooth.start-support-services",
            "Fix_Bluetooth_StartSupportServices",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "foreach($n in @('bthserv','BthAvctpSvc','BTAGService','DeviceAssociationService','RpcEptMapper')){ " +
                        "  try{ $s=Get-Service $n -ErrorAction Stop; if($s.StartType -eq 'Disabled'){ Set-Service $n -StartupType Manual }; " +
                        "    if($s.Status -ne 'Running'){ Start-Service $n -ErrorAction SilentlyContinue } }catch{} " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(3))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync("sc.exe", ["query", "bthserv"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success),
        new DelegateFixAction("bluetooth.restart-device", "Fix_Bluetooth_RestartDevice", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "bluetooth-device", "bluetooth.restart-device.target", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("pnputil.exe", ["/restart-device", context.Parameters["bluetooth.restart-device.target"]], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/instanceid", context.Parameters["bluetooth.restart-device.target"]], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
            requiresTarget: true)
    ];
}

public sealed class DiskStorageModule : WindowsModuleBase
{
    private const string Id = "disk";

    public DiskStorageModule() : base(
        new ModuleInfo(Id, "Module_Disk_Name", "Module_Disk_Description", "disk.svg", 5),
        CreateChecks(), CreateFixes(),
        [new Playbook(
            "disk.slow",
            Id,
            "Symptom_Disk_Slow",
            ["disk.health", "disk.filesystem", "disk.space", "disk.usage", "disk.online-scan"],
            ["disk.clean-temp", "disk.online-scan-fix", "disk.schedule-chkdsk"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("disk.health", "Check_Disk_Health", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-PhysicalDisk | ForEach-Object {[pscustomobject]@{FriendlyName=$_.FriendlyName;MediaType=[string]$_.MediaType;HealthStatus=[string]$_.HealthStatus;OperationalStatus=[string]($_.OperationalStatus -join ',');Size=$_.Size}}", ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root).Where(row => !string.Equals(ModuleHelpers.GetString(row, "HealthStatus"), "Healthy", StringComparison.OrdinalIgnoreCase)).ToArray();
            return bad.Length > 0 ? ModuleHelpers.Finding("disk.health", Id, Severity.Critical, "Finding_Disk_HealthCritical", Join(bad)) : null;
        }),
        new DelegateDiagnosticCheck("disk.filesystem", "Check_Disk_FileSystem", Id, (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var drive = SystemVolume();
            var format = new DriveInfo(drive).DriveFormat;
            if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Finding?>(null);
            }

            return Task.FromResult<Finding?>(VolumeHealthProbe.IsDirty(drive)
                ? ModuleHelpers.Finding(
                    "disk.filesystem",
                    Id,
                    Severity.Critical,
                    "Finding_Disk_FileSystemDirty",
                    $"Volume={drive}; FileSystem={format}; Dirty=True",
                    "disk.schedule-chkdsk")
                : null);
        }),
        new DelegateDiagnosticCheck("disk.space", "Check_Disk_Space", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-Volume | Where-Object {$_.DriveLetter} | Select-Object DriveLetter,Size,SizeRemaining,HealthStatus", ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root).Where(row =>
            {
                var size = Double(row, "Size");
                return size > 0 && Double(row, "SizeRemaining") * 100 / size < context.Thresholds.Get("disk.freeSpaceWarningPercent", 10);
            }).ToArray();
            return bad.Length > 0 ? ModuleHelpers.Finding("disk.space", Id, Severity.Warning, "Finding_Disk_LowSpace", Join(bad), "disk.clean-temp") : null;
        }),
        new DelegateDiagnosticCheck("disk.online-scan", "Check_Disk_OnlineScan", Id, async (context, ct) =>
        {
            // Microsoft: chkdsk /scan runs an online scan without forcing a dismount.
            var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var letter = system.TrimEnd('\\');
            var result = await context.Commands.RunAsync(
                "chkdsk.exe",
                [letter, "/scan"],
                TimeSpan.FromMinutes(20),
                ct).ConfigureAwait(false);
            var output = (result.StdOut + Environment.NewLine + result.StdErr).Trim();
            // Microsoft online /scan: exit 0 without problem language is clean. Do not treat
            // generic TR "bulunamadı" alone as healthy — that substring is too broad.
            if (SystemCommandResultParsers.IsChkdskOnlineScanClean(result.StdOut, result.StdErr, result.ExitCode))
            {
                return null;
            }

            return ModuleHelpers.Finding(
                "disk.online-scan",
                Id,
                Severity.Warning,
                "Finding_Disk_OnlineScanIssues",
                string.IsNullOrWhiteSpace(output) ? $"chkdsk exit={result.ExitCode}" : output,
                "disk.online-scan-fix",
                "disk.schedule-chkdsk");
        }, quick: false, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("disk.usage", "Check_Disk_Usage", Id, async (context, ct) =>
        {
            const string script = "$samples=Get-Counter '\\PhysicalDisk(_Total)\\% Disk Time' -SampleInterval 1 -MaxSamples 3 -ErrorAction SilentlyContinue; $avg=($samples.CounterSamples.CookedValue|Measure-Object -Average).Average; [pscustomobject]@{Average=[math]::Round($avg,1);SysMain=(Get-Service SysMain -ErrorAction SilentlyContinue).Status;Search=(Get-Service WSearch -ErrorAction SilentlyContinue).Status}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            return row.ValueKind == JsonValueKind.Object && Double(row, "Average") > 90
                ? ModuleHelpers.Finding("disk.usage", Id, Severity.Warning, "Finding_Disk_HighUsage", row.ToString())
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction(
            "disk.online-scan-fix",
            "Fix_Disk_OnlineScanFix",
            Id,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "disk-online-scan-fix", ct),
            async (context, ct) =>
            {
                var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var letter = system.TrimEnd('\\');
                // Microsoft: /spotfix attempts online repair after /scan findings.
                return await ModuleHelpers.RunSequenceAsync(
                    context,
                    [
                        new CommandStep("chkdsk.exe", [letter, "/scan"], TimeSpan.FromMinutes(20)),
                        new CommandStep("chkdsk.exe", [letter, "/spotfix"], TimeSpan.FromMinutes(30))
                        {
                            AcceptedExitCodes = new HashSet<int> { 0, 1, 2, 3 }
                        }
                    ],
                    ct).ConfigureAwait(false);
            },
            async (context, ct) =>
            {
                var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var letter = system.TrimEnd('\\');
                var result = await context.Commands.RunAsync(
                    "chkdsk.exe",
                    [letter, "/scan"],
                    TimeSpan.FromMinutes(20),
                    ct).ConfigureAwait(false);
                return SystemCommandResultParsers.IsChkdskOnlineScanClean(
                    result.StdOut,
                    result.StdErr,
                    result.ExitCode);
            }),
        new DelegateFixAction("disk.clean-temp", "Fix_Disk_CleanTemp", Id, RiskTier.Safe,
            BackupTempFilesAsync,
            ApplyTempCleanupAsync,
            VerifyTempCleanupAsync),
        new DelegateFixAction("disk.schedule-chkdsk", "Fix_Disk_ScheduleChkdsk", Id, RiskTier.Aggressive,
            (context, ct) => context.Backups.CaptureRegistryAsync(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("chkntfs.exe", ["/c", SystemVolume()], TimeSpan.FromMinutes(2))], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync("chkntfs.exe", [SystemVolume()], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                return result.Success && !string.IsNullOrWhiteSpace(result.StdOut);
            },
            requiresReboot: true),
        ExpandedRepairHelpers.TransientCommand(
            "disk.clean-windows-temp",
            "Fix_Disk_CleanWindowsTemp",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; $w=Join-Path $env:windir 'Temp'; " +
                        "if(Test-Path -LiteralPath $w){ Get-ChildItem -LiteralPath $w -Force -ErrorAction SilentlyContinue | " +
                        "  Where-Object { $_.LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddHours(-24) } | " +
                        "  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ],
            async (_, _) => await Task.FromResult(true)),
        ExpandedRepairHelpers.TransientCommand(
            "disk.clean-user-temp",
            "Fix_Disk_CleanUserTemp",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; $t=[IO.Path]::GetTempPath().TrimEnd('\\'); " +
                        "if(Test-Path -LiteralPath $t){ Get-ChildItem -LiteralPath $t -Force -ErrorAction SilentlyContinue | " +
                        "  Where-Object { $_.LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddHours(-24) } | " +
                        "  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ],
            async (_, _) => await Task.FromResult(true)),
        ExpandedRepairHelpers.TransientCommand(
            "disk.flush-volume",
            "Fix_Disk_FlushVolume",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "try{ Write-VolumeCache -DriveLetter ((Get-Item $env:SystemRoot).PSDrive.Name) -ErrorAction Stop }catch{}; exit 0"
                    ],
                    TimeSpan.FromMinutes(2))
            ],
            async (_, _) => await Task.FromResult(true)),
        ExpandedRepairHelpers.TransientCommand(
            "disk.spotfix-only",
            "Fix_Disk_SpotFixOnly",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "chkdsk.exe",
                    [SystemVolume(), "/spotfix"],
                    TimeSpan.FromMinutes(30))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1, 2, 3 }
                }
            ],
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync(
                    "chkdsk.exe", [SystemVolume(), "/scan"], TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
                return SystemCommandResultParsers.IsChkdskOnlineScanClean(result.StdOut, result.StdErr, result.ExitCode);
            }),
        ExpandedRepairHelpers.TransientCommand(
            "disk.fsutil-dirty-query",
            "Fix_Disk_ClearVolumeHints",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("fsutil.exe", ["volume", "diskfree", SystemVolume()])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                },
                new CommandStep("fsutil.exe", ["dirty", "query", SystemVolume()])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ],
            async (_, _) => await Task.FromResult(true))
    ];

    private static string SystemVolume()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
        if (root is null || root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
        {
            throw new InvalidOperationException("The Windows system volume could not be resolved safely.");
        }

        return $"{char.ToUpperInvariant(root[0])}:";
    }

    private static async Task<BackupEntry?> BackupTempFilesAsync(FixContext context, CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var quarantine = Path.Combine(dir, "TempQuarantine");
        Directory.CreateDirectory(quarantine);
        var roots = new[]
        {
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        const int maxFiles = 500;
        const long maxBytes = 512L * 1024 * 1024;
        long copiedBytes = 0;
        var copied = new List<TempCopy>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (copied.Count >= maxFiles || copiedBytes >= maxBytes) break;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-24)) continue;
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var length = new FileInfo(file).Length;
                    if (length > maxBytes || copiedBytes + length > maxBytes) continue;
                    var target = Path.Combine(quarantine, $"{copied.Count:00000}-{Path.GetFileName(file)}");
                    File.Copy(file, target, false);
                    var backupHash = await HashFileAsync(target, ct).ConfigureAwait(false);
                    var originalHash = await HashFileAsync(file, ct).ConfigureAwait(false);
                    if (!string.Equals(backupHash, originalHash, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(target);
                        continue;
                    }
                    copied.Add(new TempCopy(Path.GetFullPath(file), Path.GetFullPath(target), backupHash));
                    copiedBytes += length;
                }
                catch { }
            }
        }
        if (copied.Count == 0) return await ModuleHelpers.TransientMarkerAsync(context, "empty-temp", ct).ConfigureAwait(false);
        var map = Path.Combine(quarantine, "temp-map.json");
        await File.WriteAllTextAsync(map, JsonSerializer.Serialize(copied), ct).ConfigureAwait(false);
        var escapedMap = map.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$ErrorActionPreference='Stop'; $failed=0; $items=Get-Content -Raw -LiteralPath '{escapedMap}'|ConvertFrom-Json; @($items)|ForEach-Object {{if((Test-Path -LiteralPath $_.Backup) -and -not (Test-Path -LiteralPath $_.Original)){{Copy-Item -LiteralPath $_.Backup -Destination $_.Original -Force -ErrorAction Stop}}else{{$failed++}}}}; if($failed -gt 0){{exit 3}}";
        return await context.Backups.CaptureExistingCommandStateAsync(
            "Temporary files", quarantine, "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], ct).ConfigureAwait(false);
    }

    private static async Task<FixResult> ApplyTempCleanupAsync(FixContext context, CancellationToken ct)
    {
        var map = Path.Combine(ModuleHelpers.BackupDirectory(context), "TempQuarantine", "temp-map.json");
        if (!File.Exists(map)) return FixResult.Ok("FixResult_Applied", "deleted=0; changedOrUnavailable=0");

        var items = JsonSerializer.Deserialize<List<TempCopy>>(await File.ReadAllTextAsync(map, ct).ConfigureAwait(false)) ?? [];
        if (!ValidateTempMap(context, items)) return FixResult.Fail("FixResult_ApplyFailed", "The temporary-file backup map did not pass validation.");

        var deleted = 0;
        var skipped = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(item.Original) ||
                    !string.Equals(await HashFileAsync(item.Original, ct).ConfigureAwait(false), item.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }
                File.Delete(item.Original);
                deleted++;
            }
            catch
            {
                skipped++;
            }
        }
        return FixResult.Ok("FixResult_Applied", $"deleted={deleted}; changedOrUnavailable={skipped}");
    }

    private static async Task<bool> VerifyTempCleanupAsync(FixContext context, CancellationToken ct)
    {
        var map = Path.Combine(ModuleHelpers.BackupDirectory(context), "TempQuarantine", "temp-map.json");
        if (!File.Exists(map)) return true;
        try
        {
            var items = JsonSerializer.Deserialize<List<TempCopy>>(await File.ReadAllTextAsync(map, ct).ConfigureAwait(false)) ?? [];
            if (!ValidateTempMap(context, items)) return false;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(item.Original) &&
                    string.Equals(await HashFileAsync(item.Original, ct).ConfigureAwait(false), item.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
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

    private static bool ValidateTempMap(FixContext context, IReadOnlyList<TempCopy> items)
    {
        if (items.Count is 0 or > 500) return false;
        var quarantine = Path.GetFullPath(Path.Combine(ModuleHelpers.BackupDirectory(context), "TempQuarantine"));
        var roots = new[]
        {
            Path.GetFullPath(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
        };
        return items.All(item =>
        {
            try
            {
                var original = Path.GetFullPath(item.Original);
                var backup = Path.GetFullPath(item.Backup);
                return item.ContentHash.Length == 64 && item.ContentHash.All(Uri.IsHexDigit) &&
                       roots.Any(root => string.Equals(Path.GetDirectoryName(original), root, StringComparison.OrdinalIgnoreCase)) &&
                       string.Equals(Path.GetDirectoryName(backup), quarantine, StringComparison.OrdinalIgnoreCase) &&
                       File.Exists(backup) && !File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        });
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private sealed record TempCopy(string Original, string Backup, string ContentHash);
}

public sealed class SystemIntegrityModule : WindowsModuleBase
{
    private const string Id = "integrity";

    public SystemIntegrityModule() : base(
        new ModuleInfo(Id, "Module_Integrity_Name", "Module_Integrity_Description", "integrity.svg", 6),
        CreateChecks(), CreateFixes(),
        [
            new Playbook(
                "integrity.corrupt",
                Id,
                "Symptom_Integrity_Corrupt",
                ["integrity.component-store", "integrity.scan-health", "integrity.analyze-store"],
                ["integrity.dism-restore", "integrity.sfc-scan", "integrity.dism-sfc"]),
            new Playbook(
                "integrity.cleanup",
                Id,
                "Symptom_Integrity_Cleanup",
                ["integrity.analyze-store"],
                ["integrity.component-cleanup"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("integrity.component-store", "Check_Integrity_ComponentStore", Id, async (context, ct) =>
        {
            // Microsoft: CheckHealth is a fast flag check (not a deep scan).
            var result = await context.Commands.RunAsync(
                "dism.exe",
                ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                TimeSpan.FromMinutes(5),
                ct).ConfigureAwait(false);
            return CreateHealthFinding(
                "integrity.component-store",
                result,
                defaultMessageKey: "Finding_Integrity_Repairable");
        }),
        new DelegateDiagnosticCheck(
            "integrity.scan-health",
            "Check_Integrity_ScanHealth",
            Id,
            async (context, ct) =>
            {
                // Microsoft: ScanHealth walks the component store (can take many minutes).
                // A successful "No component store corruption detected" must never raise a finding.
                var result = await context.Commands.RunAsync(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/ScanHealth", "/English"],
                    TimeSpan.FromMinutes(30),
                    ct).ConfigureAwait(false);
                return CreateHealthFinding(
                    "integrity.scan-health",
                    result,
                    defaultMessageKey: "Finding_Integrity_ScanHealthFailed");
            },
            quick: false,
            supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck(
            "integrity.analyze-store",
            "Check_Integrity_AnalyzeStore",
            Id,
            async (context, ct) =>
            {
                // Microsoft: AnalyzeComponentStore is size/cleanup analysis — not corruption.
                // "Cleanup Recommended : Yes" → StartComponentCleanup only (not RestoreHealth).
                var result = await context.Commands.RunAsync(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"],
                    TimeSpan.FromMinutes(20),
                    ct).ConfigureAwait(false);
                var detail = BuildDismDetail(result);
                if (!DismHealthParser.IsCleanupRecommended(result.StdOut, result.StdErr))
                {
                    return null;
                }

                var reclaimable = DismHealthParser.TryParseReclaimablePackages(result.StdOut, result.StdErr);
                var summary = reclaimable is int count
                    ? $"Component Store Cleanup Recommended : Yes{Environment.NewLine}Reclaimable packages: {count}{Environment.NewLine}{Environment.NewLine}{detail}"
                    : detail;
                return ModuleHelpers.Finding(
                    "integrity.analyze-store",
                    Id,
                    Severity.Info,
                    "Finding_Integrity_CleanupRecommended",
                    summary,
                    "integrity.component-cleanup");
            },
            quick: false,
            supportsPostRepairVerification: false)
    ];

    private static Finding? CreateHealthFinding(string checkId, CmdResult result, string defaultMessageKey)
    {
        var health = DismHealthParser.ParseHealthScan(
            result.StdOut,
            result.StdErr,
            result.ExitCode,
            result.TimedOut);
        if (!DismHealthParser.NeedsCorruptionRepair(health))
        {
            return null;
        }

        var detail = BuildDismDetail(result);
        var (messageKey, severity, fixes) = health switch
        {
            DismHealthParser.ComponentStoreHealth.NotRepairable => (
                "Finding_Integrity_NotRepairable",
                Severity.Critical,
                new[] { "integrity.dism-sfc", "integrity.dism-restore", "integrity.sfc-scan" }),
            DismHealthParser.ComponentStoreHealth.Repairable => (
                defaultMessageKey,
                Severity.Warning,
                new[] { "integrity.dism-restore", "integrity.sfc-scan", "integrity.dism-sfc" }),
            _ => (
                "Finding_Integrity_DismFailed",
                Severity.Warning,
                new[] { "integrity.dism-restore", "integrity.sfc-scan" })
        };

        // Cleanup is a separate finding — do not attach component-cleanup to corruption results.
        return ModuleHelpers.Finding(checkId, Id, severity, messageKey, detail, fixes);
    }

    private static string BuildDismDetail(CmdResult result)
    {
        var body = (result.StdOut + Environment.NewLine + result.StdErr).Trim();
        return
            $"DISM exit={result.ExitCode}; timedOut={result.TimedOut}; duration={result.Duration.TotalSeconds:0}s{Environment.NewLine}" +
            body;
    }

    private static bool IsComponentStoreHealthy(CmdResult health) =>
        DismHealthParser.ParseHealthScan(
            health.StdOut,
            health.StdErr,
            health.ExitCode,
            health.TimedOut) == DismHealthParser.ComponentStoreHealth.Healthy;

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction(
            "integrity.sfc-scan",
            "Fix_Integrity_SfcScan",
            Id,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-sfc-scan", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(
                context,
                [new CommandStep("sfc.exe", ["/scannow"], TimeSpan.FromMinutes(45))],
                ct),
            async (context, ct) =>
            {
                var verify = await context.Commands.RunAsync(
                    "sfc.exe",
                    ["/verifyonly"],
                    TimeSpan.FromMinutes(45),
                    ct).ConfigureAwait(false);
                var outcome = SystemCommandResultParsers.ParseSfc(
                    verify.StdOut,
                    verify.StdErr,
                    verify.ExitCode);
                return SystemCommandResultParsers.IsSfcAcceptable(outcome);
            },
            requiresReboot: true),
        new DelegateFixAction(
            "integrity.dism-restore",
            "Fix_Integrity_DismRestore",
            Id,
            RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-dism-restore", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(
                context,
                [
                    new CommandStep(
                        "dism.exe",
                        ["/Online", "/Cleanup-Image", "/RestoreHealth", "/English"],
                        TimeSpan.FromMinutes(60))
                ],
                ct),
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(10),
                    ct).ConfigureAwait(false);
                return IsComponentStoreHealthy(health);
            },
            requiresReboot: true),
        new DelegateFixAction(
            "integrity.component-cleanup",
            "Fix_Integrity_ComponentCleanup",
            Id,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-component-cleanup", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(
                context,
                [
                    new CommandStep(
                        "dism.exe",
                        ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English"],
                        TimeSpan.FromMinutes(45))
                ],
                ct),
            async (context, ct) =>
            {
                // Cleanup success: command completed; re-analyze is slow — CheckHealth is enough safety.
                var health = await context.Commands.RunAsync(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(10),
                    ct).ConfigureAwait(false);
                return health.Success || IsComponentStoreHealthy(health);
            }),
        new DelegateFixAction(
            "integrity.dism-sfc",
            "Fix_Integrity_RepairChain",
            Id,
            RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-dism-sfc", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(
                context,
                [
                    new CommandStep("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth", "/English"], TimeSpan.FromMinutes(60)),
                    new CommandStep("sfc.exe", ["/scannow"], TimeSpan.FromMinutes(45))
                ],
                ct),
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(10),
                    ct).ConfigureAwait(false);
                return IsComponentStoreHealthy(health);
            },
            requiresReboot: true),
        // Reverse order chain used when SFC is preferred first (MS community workflow variant).
        new DelegateFixAction(
            "integrity.sfc-dism",
            "Fix_Integrity_SfcThenDism",
            Id,
            RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-sfc-dism", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("sfc.exe", ["/scannow"], TimeSpan.FromMinutes(45)),
                new CommandStep("dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth", "/English"], TimeSpan.FromMinutes(60))
            ], ct),
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return IsComponentStoreHealthy(health);
            },
            requiresReboot: true),
        // Microsoft: StartComponentCleanup /ResetBase reclaims superseded components (more aggressive).
        new DelegateFixAction(
            "integrity.component-cleanup-resetbase",
            "Fix_Integrity_ComponentCleanupResetBase",
            Id,
            RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "integrity-cleanup-resetbase", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "dism.exe",
                    ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase", "/English"],
                    TimeSpan.FromMinutes(60))
            ], ct),
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
                return health.Success || IsComponentStoreHealthy(health);
            },
            requiresReboot: true),
        ExpandedRepairHelpers.TransientCommand(
            "integrity.checkhealth-refresh",
            "Fix_Integrity_CheckHealthRefresh",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"], TimeSpan.FromMinutes(5))
            ],
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                return IsComponentStoreHealthy(health) || health.Success;
            }),
        ExpandedRepairHelpers.TransientCommand(
            "integrity.sfc-verifyonly",
            "Fix_Integrity_SfcVerifyOnly",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("sfc.exe", ["/verifyonly"], TimeSpan.FromMinutes(45))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ],
            async (context, ct) =>
            {
                var verify = await context.Commands.RunAsync(
                    "sfc.exe", ["/verifyonly"], TimeSpan.FromMinutes(45), ct).ConfigureAwait(false);
                return SystemCommandResultParsers.IsSfcAcceptable(
                    SystemCommandResultParsers.ParseSfc(verify.StdOut, verify.StdErr, verify.ExitCode));
            }),
        ExpandedRepairHelpers.TransientCommand(
            "integrity.scanhealth-only",
            "Fix_Integrity_ScanHealthOnly",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("dism.exe", ["/Online", "/Cleanup-Image", "/ScanHealth", "/English"], TimeSpan.FromMinutes(30))
            ],
            async (context, ct) =>
            {
                var health = await context.Commands.RunAsync(
                    "dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"],
                    TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
                return IsComponentStoreHealthy(health) || health.Success;
            })
    ];
}

public sealed class StoreAppsModule : WindowsModuleBase
{
    private const string Id = "store";

    public StoreAppsModule() : base(
        new ModuleInfo(Id, "Module_Store_Name", "Module_Store_Description", "store.svg", 7),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("store.not-open", Id, "Symptom_Store_NotOpen",
                ["store.package", "store.errors"],
                ["store.restart-services", "store.reset-cache", "store.clear-local-cache", "store.reset-appx-package", "store.register-manifest"]),
            new Playbook("store.install-fail", Id, "Symptom_Store_InstallFail",
                ["store.package", "store.errors"],
                ["store.restart-services", "store.start-appx-services", "store.reset-cache", "store.clear-local-cache"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("store.package", "Check_Store_Package", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-AppxPackage Microsoft.WindowsStore -AllUsers | Select-Object Name,PackageFullName,InstallLocation,Status", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            if (rows.Length == 0) return ModuleHelpers.Finding("store.package", Id, Severity.Warning, "Finding_Store_Missing", string.Empty,
                "store.register-manifest", "store.reset-cache", "store.restart-services");
            var unhealthy = rows.Where(row =>
            {
                var status = ModuleHelpers.GetString(row, "Status");
                return !string.IsNullOrWhiteSpace(status) && !status.Equals("Ok", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            return unhealthy.Length > 0
                ? ModuleHelpers.Finding("store.package", Id, Severity.Warning, "Finding_Store_PackageUnhealthy", Join(unhealthy),
                    "store.reset-appx-package", "store.reset-cache", "store.restart-services")
                : null;
        }),
        new DelegateDiagnosticCheck("store.errors", "Check_Store_Errors", Id, async (context, ct) =>
        {
            const string script = "$since=(Get-Date).AddDays(-3); Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Store/Operational';StartTime=$since;Level=2} -MaxEvents 8 -ErrorAction SilentlyContinue | Select-Object TimeCreated,Id,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding("store.errors", Id, Severity.Warning, "Finding_Store_RecentErrors", Join(rows),
                    "store.restart-services", "store.reset-cache", "store.clear-local-cache")
                : null;
        }, supportsPostRepairVerification: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("store.reset-cache", "Fix_Store_ResetCache", Id, RiskTier.Safe,
            BackupStoreCacheAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context, [new CommandStep("wsreset.exe", [], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) => (await context.Commands.RunAsync("powershell.exe", ["-NoProfile", "-Command", "if(Get-AppxPackage Microsoft.WindowsStore -AllUsers){exit 0}else{exit 1}"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success),
        // Microsoft Store depends on AppX deployment + licensing services.
        ExpandedRepairHelpers.RestartNamedServices(
            "store.restart-services", "Fix_Store_RestartServices", Id,
            ["AppXSvc", "ClipSVC", "InstallService", "StateRepository", "TokenBroker"], "AppXSvc"),
        ExpandedRepairHelpers.TransientCommand(
            "store.start-appx-services",
            "Fix_Store_StartAppxServices",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "foreach($n in @('StateRepository','AppXSvc','ClipSVC','InstallService','wuauserv','BITS')){ " +
                        "  try{ $s=Get-Service $n -EA Stop; if($s.StartType -eq 'Disabled'){ Set-Service $n -StartupType Manual }; " +
                        "    if($s.Status -ne 'Running'){ Start-Service $n -EA SilentlyContinue } }catch{} }; exit 0"
                    ],
                    TimeSpan.FromMinutes(4))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync("sc.exe", ["query", "AppXSvc"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success),
        ExpandedRepairHelpers.TransientCommand(
            "store.clear-local-cache",
            "Fix_Store_ClearLocalCache",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "$roots=@(Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Microsoft.WindowsStore*' -ErrorAction SilentlyContinue); " +
                        "foreach($r in $roots){ $cache=Join-Path $r.FullName 'LocalCache'; if(Test-Path -LiteralPath $cache){ " +
                        "  Get-ChildItem -LiteralPath $cache -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } }; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-Command", "if(Get-AppxPackage Microsoft.WindowsStore){exit 0}else{exit 1}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success),
        // Windows 11+: Reset-AppxPackage; older builds no-op safely.
        ExpandedRepairHelpers.TransientCommand(
            "store.reset-appx-package",
            "Fix_Store_ResetAppxPackage",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "$pkg=Get-AppxPackage -Name Microsoft.WindowsStore -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                        "if($null -eq $pkg){ exit 0 }; " +
                        "if(Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue){ " +
                        "  try{ Reset-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop }catch{} " +
                        "} else { " +
                        "  $cache=Join-Path $pkg.InstallLocation '..\\..\\LocalCache' -ErrorAction SilentlyContinue " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-Command", "if(Get-AppxPackage Microsoft.WindowsStore){exit 0}else{exit 1}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success),
        // Microsoft Q&A: register Store AppxManifest from InstallLocation.
        ExpandedRepairHelpers.TransientCommand(
            "store.register-manifest",
            "Fix_Store_RegisterManifest",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Get-AppxPackage Microsoft.WindowsStore -AllUsers -ErrorAction SilentlyContinue | ForEach-Object { " +
                        "  $manifest=Join-Path $_.InstallLocation 'AppxManifest.xml'; " +
                        "  if(Test-Path -LiteralPath $manifest){ " +
                        "    try{ Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction SilentlyContinue }catch{} " +
                        "  } " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(10))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "powershell.exe",
                    ["-NoProfile", "-Command", "if(Get-AppxPackage Microsoft.WindowsStore){exit 0}else{exit 1}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success)
    ];

    private static async Task<BackupEntry?> BackupStoreCacheAsync(FixContext context, CancellationToken ct)
    {
        const long maximumBytes = 512L * 1024 * 1024;
        const int maximumEntries = 50_000;
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.WindowsStore_8wekyb3d8bbwe",
            "LocalCache");
        var root = Path.Combine(ModuleHelpers.BackupDirectory(context), $"StoreCache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        if (Directory.Exists(source))
        {
            if (!TryMeasureDirectories([source], maximumBytes, maximumEntries, out var bytes) ||
                !HasFreeSpace(root, bytes + 64L * 1024 * 1024))
            {
                return null;
            }

            try
            {
                await CopyDirectorySnapshotAsync(
                    source,
                    Path.Combine(root, "cache"),
                    maximumBytes,
                    maximumEntries,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        var escapedSource = source.Replace("'", "''", StringComparison.Ordinal);
        var restore = $"$ErrorActionPreference='Stop'; $source='{escapedSource}'; $cache=Join-Path '{{backup}}' 'cache'; if(Test-Path -LiteralPath $source){{Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction Stop}}; if(Test-Path -LiteralPath $cache){{Copy-Item -LiteralPath $cache -Destination $source -Recurse -Force -ErrorAction Stop}}";
        return await context.Backups.CaptureExistingCommandStateAsync(
            "Microsoft Store cache",
            root,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", restore],
            ct).ConfigureAwait(false);
    }

    private static async Task CopyDirectorySnapshotAsync(
        string source,
        string destination,
        long maximumBytes,
        int maximumEntries,
        CancellationToken ct)
    {
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(source));
        long copiedBytes = 0;
        var entries = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("A reparse point was found in the Store cache.");
            }

            var relativeDirectory = current.Length < sourceRoot.Length
                ? string.Empty
                : Path.GetRelativePath(sourceRoot, current);
            Directory.CreateDirectory(Path.Combine(destination, relativeDirectory));
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                entries++;
                if (entries > maximumEntries) throw new IOException("The Store cache exceeded the entry limit.");
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("A reparse point was found in the Store cache.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }

                var info = new FileInfo(entry);
                copiedBytes = checked(copiedBytes + info.Length);
                if (copiedBytes > maximumBytes) throw new IOException("The Store cache exceeded the size limit.");
                var relative = Path.GetRelativePath(sourceRoot, entry);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }
    }
}

public sealed class TimeSyncModule : WindowsModuleBase
{
    private const string Id = "time";

    public TimeSyncModule() : base(
        new ModuleInfo(Id, "Module_Time_Name", "Module_Time_Description", "time.svg", 8),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("time.wrong", Id, "Symptom_Time_Wrong",
                ["time.service", "time.source"],
                ["time.restart-service", "time.resync", "time.resync-rediscover", "time.set-manual-ntp", "time.re-register"]),
            new Playbook("time.no-source", Id, "Symptom_Time_NoSource",
                ["time.source", "time.service"],
                ["time.set-manual-ntp", "time.config-update", "time.re-register", "time.resync"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("time.service", "Check_Time_Service", Id, async (context, ct) =>
        {
            var result = await context.Commands.RunAsync("w32tm.exe", ["/query", "/status"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            var detail = (result.StdOut + Environment.NewLine + result.StdErr).Trim();
            var notSyncing =
                !result.Success ||
                detail.Contains("The service has not been started", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("not been started", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("Free-running System Clock", StringComparison.OrdinalIgnoreCase);
            return notSyncing
                ? ModuleHelpers.Finding(
                    "time.service",
                    Id,
                    Severity.Warning,
                    "Finding_Time_NotSynchronized",
                    string.IsNullOrWhiteSpace(detail) ? result.StdErr : detail,
                    "time.restart-service", "time.resync", "time.re-register", "time.resync-rediscover")
                : null;
        }),
        new DelegateDiagnosticCheck("time.source", "Check_Time_Source", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                "Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\W32Time\\Parameters' -ErrorAction Stop | Select-Object Type,NtpServer",
                ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            var type = ModuleHelpers.GetString(row, "Type") ?? string.Empty;
            var server = ModuleHelpers.GetString(row, "NtpServer") ?? string.Empty;
            return row.ValueKind != JsonValueKind.Object ||
                   type.Equals("NoSync", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("NTP", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(server)
                ? ModuleHelpers.Finding("time.source", Id, Severity.Warning, "Finding_Time_LocalClock", row.ToString(),
                    "time.set-manual-ntp", "time.config-update", "time.re-register", "time.resync")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("time.resync", "Fix_Time_Resync", Id, RiskTier.Safe,
            async (context, ct) =>
            {
                var service = await context.Backups.CaptureServicesAsync(["W32Time"], ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
                var registry = await context.Backups.CaptureRegistryAsync(@"HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Parameters", ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
                return service is not null && registry is not null
                    ? await context.Backups.CaptureBundleAsync("Windows Time", [service, registry], ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false)
                    : null;
            },
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$service=Get-CimInstance Win32_Service -Filter \"Name='W32Time'\" -ErrorAction Stop; if($service.StartMode -eq 'Disabled'){Set-Service W32Time -StartupType Manual}; Start-Service W32Time"]), new CommandStep("w32tm.exe", ["/resync", "/force"], TimeSpan.FromMinutes(2))], ct),
            async (context, ct) => (await context.Commands.RunAsync("w32tm.exe", ["/query", "/status"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success),
        ExpandedRepairHelpers.RestartNamedServices(
            "time.restart-service", "Fix_Time_RestartService", Id,
            ["W32Time"], "W32Time"),
        // Microsoft: w32tm /resync /rediscover rediscovers network time sources first.
        ExpandedRepairHelpers.TransientCommand(
            "time.resync-rediscover",
            "Fix_Time_ResyncRediscover",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "try{ if((Get-Service W32Time).Status -ne 'Running'){ Start-Service W32Time } }catch{}; exit 0"]),
                new CommandStep("w32tm.exe", ["/resync", "/rediscover", "/force"], TimeSpan.FromMinutes(3))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync("w32tm.exe", ["/query", "/status"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success),
        // Microsoft: /config /manualpeerlist with 0x8 client mode for public NTP.
        new DelegateFixAction(
            "time.set-manual-ntp",
            "Fix_Time_SetManualNtp",
            Id,
            RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureRegistryAsync(
                @"HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Parameters",
                ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("w32tm.exe", ["/config", "/manualpeerlist:time.windows.com,0x8", "/syncfromflags:manual", "/update"]),
                new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "try{ Restart-Service W32Time -Force }catch{ Start-Service W32Time }; exit 0"]),
                new CommandStep("w32tm.exe", ["/resync", "/force"], TimeSpan.FromMinutes(2))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync(
                    "w32tm.exe", ["/query", "/source"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                return result.Success ||
                       (result.StdOut + result.StdErr).Contains("time.windows.com", StringComparison.OrdinalIgnoreCase);
            }),
        ExpandedRepairHelpers.TransientCommand(
            "time.config-update",
            "Fix_Time_ConfigUpdate",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("w32tm.exe", ["/config", "/update"]),
                new CommandStep("w32tm.exe", ["/resync", "/force"], TimeSpan.FromMinutes(2))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync("w32tm.exe", ["/query", "/status"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success),
        // Microsoft / Dell KB: unregister + register restores default W32Time configuration.
        new DelegateFixAction(
            "time.re-register",
            "Fix_Time_ReRegister",
            Id,
            RiskTier.Moderate,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var service = await context.Backups.CaptureServicesAsync(["W32Time"], dir, ct).ConfigureAwait(false);
                var registry = await context.Backups.CaptureRegistryAsync(
                    @"HKLM\SYSTEM\CurrentControlSet\Services\W32Time", dir, ct).ConfigureAwait(false);
                return service is not null && registry is not null
                    ? await context.Backups.CaptureBundleAsync("w32time-reregister", [service, registry], dir, ct)
                        .ConfigureAwait(false)
                    : service ?? registry;
            },
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "try{ Stop-Service W32Time -Force -ErrorAction SilentlyContinue }catch{}; exit 0"]),
                new CommandStep("w32tm.exe", ["/unregister"])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1, 2 }
                },
                new CommandStep("w32tm.exe", ["/register"]),
                new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "try{ Set-Service W32Time -StartupType Manual; Start-Service W32Time }catch{}; exit 0"]),
                new CommandStep("w32tm.exe", ["/config", "/manualpeerlist:time.windows.com,0x8", "/syncfromflags:manual", "/update"])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                },
                new CommandStep("w32tm.exe", ["/resync", "/force"], TimeSpan.FromMinutes(2))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ], ct),
            async (context, ct) =>
                (await context.Commands.RunAsync("sc.exe", ["query", "W32Time"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success)
    ];
}

public sealed class StartupPerformanceModule : WindowsModuleBase
{
    private const string Id = "performance";

    /// <summary>
    /// Safe-to-restart core Windows services (never RpcSs/DcomLaunch/LSM/etc.).
    /// </summary>
    private static readonly string[] CoreWindowsServices =
    [
        "Dhcp", "Dnscache", "NlaSvc", "netprofm", "WlanSvc",
        "AudioEndpointBuilder", "Audiosrv",
        "EventLog", "PlugPlay", "DsmSvc", "DeviceInstall",
        "Spooler", "BITS", "cryptsvc", "wuauserv",
        "W32Time", "bthserv", "WSearch", "LanmanWorkstation", "Winmgmt",
        "Schedule", "Themes", "FontCache", "BrokerInfrastructure"
    ];

    public StartupPerformanceModule() : base(
        new ModuleInfo(Id, "Module_Performance_Name", "Module_Performance_Description", "performance.svg", 9),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("performance.slow-start", Id, "Symptom_Performance_SlowStartup",
                ["performance.startup", "performance.power", "performance.services"],
                ["performance.balanced-plan", "performance.repair-core-services"]),
            new Playbook("performance.services", Id, "Symptom_Performance_Services",
                ["performance.services"],
                ["performance.repair-core-services", "performance.restart-core-services"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("performance.startup", "Check_Performance_Startup", Id, async (context, ct) =>
        {
            const string script = "$items=@(); $items += Get-CimInstance Win32_StartupCommand | Select-Object Name,Command,Location,User; $items";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 15 ? ModuleHelpers.Finding("performance.startup", Id, Severity.Info, "Finding_Performance_ManyStartupItems", $"Count={rows.Length}{Environment.NewLine}{Join(rows.Take(30))}") : null;
        }),
        new DelegateDiagnosticCheck("performance.power", "Check_Performance_PowerPlan", Id, async (context, ct) =>
        {
            var result = await context.Commands.RunAsync("powercfg.exe", ["/getactivescheme"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            return result.Success && result.StdOut.Contains("a1841308-3541-4fab-bc81-f71556f20b4a", StringComparison.OrdinalIgnoreCase)
                ? ModuleHelpers.Finding("performance.power", Id, Severity.Info, "Finding_Performance_PowerSaver", result.StdOut, "performance.balanced-plan")
                : null;
        }),
        new DelegateDiagnosticCheck("performance.services", "Check_Performance_CoreServices", Id, async (context, ct) =>
        {
            const string script =
                "$names=@('Dhcp','Dnscache','NlaSvc','AudioEndpointBuilder','Audiosrv','EventLog','PlugPlay','Spooler','BITS','cryptsvc','wuauserv','W32Time','LanmanWorkstation','Winmgmt','Schedule'); " +
                "Get-CimInstance Win32_Service | Where-Object { $_.Name -in $names } | Select-Object Name,State,StartMode";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var bad = rows.Where(row =>
            {
                var name = ModuleHelpers.GetString(row, "Name") ?? string.Empty;
                var state = ModuleHelpers.GetString(row, "State") ?? string.Empty;
                var start = ModuleHelpers.GetString(row, "StartMode") ?? string.Empty;
                // W32Time may be Manual and stopped until needed; Spooler/BITS may be Manual.
                var optionalStopped = name is "W32Time" or "BITS" or "wuauserv" or "bthserv" or "WSearch";
                return start.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
                       (!optionalStopped && !state.Equals("Running", StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            return bad.Length > 0
                ? ModuleHelpers.Finding(
                    "performance.services",
                    Id,
                    Severity.Warning,
                    "Finding_Performance_CoreServices",
                    Join(bad),
                    "performance.repair-core-services",
                    "performance.restart-core-services")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("performance.balanced-plan", "Fix_Performance_BalancedPlan", Id, RiskTier.Safe,
            BackupPowerPlanAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context, [new CommandStep("powercfg.exe", ["/setactive", "381b4222-f694-41f0-9685-ff5bb260df2e"])], ct),
            async (context, ct) => (await context.Commands.RunAsync("powercfg.exe", ["/getactivescheme"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).StdOut.Contains("381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase)),
        new DelegateFixAction("performance.repair-core-services", "Fix_Performance_RepairCoreServices", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(CoreWindowsServices, ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ApplyCoreServicesRepairAsync(context, restartRunning: false, ct),
            VerifyCoreServicesAsync),
        new DelegateFixAction("performance.restart-core-services", "Fix_Performance_RestartCoreServices", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(CoreWindowsServices, ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ApplyCoreServicesRepairAsync(context, restartRunning: true, ct),
            VerifyCoreServicesAsync),
        // Microsoft powercfg GUIDs: High performance scheme.
        new DelegateFixAction("performance.high-performance-plan", "Fix_Performance_HighPerformancePlan", Id, RiskTier.Safe,
            BackupPowerPlanAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powercfg.exe", ["/setactive", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"])], ct),
            async (context, ct) => (await context.Commands.RunAsync("powercfg.exe", ["/getactivescheme"], TimeSpan.FromMinutes(1), ct)
                .ConfigureAwait(false)).StdOut.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", StringComparison.OrdinalIgnoreCase)),
        // Microsoft: powercfg -restoredefaultschemes restores default plans.
        ExpandedRepairHelpers.TransientCommand(
            "performance.restore-default-schemes",
            "Fix_Performance_RestoreDefaultSchemes",
            Id,
            RiskTier.Moderate,
            [new CommandStep("powercfg.exe", ["-restoredefaultschemes"])],
            async (context, ct) =>
                (await context.Commands.RunAsync("powercfg.exe", ["/list"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success),
        ExpandedRepairHelpers.RestartNamedServices(
            "performance.restart-sysmain", "Fix_Performance_RestartSysMain", Id,
            ["SysMain"], "SysMain"),
        ExpandedRepairHelpers.RestartNamedServices(
            "performance.restart-schedule", "Fix_Performance_RestartSchedule", Id,
            ["Schedule"], "Schedule"),
        ExpandedRepairHelpers.TransientCommand(
            "performance.query-energy",
            "Fix_Performance_QueryEnergy",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("powercfg.exe", ["/energy", "/duration", "5"], TimeSpan.FromMinutes(3))
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ],
            async (_, _) => await Task.FromResult(true)),
        ExpandedRepairHelpers.TransientCommand(
            "performance.hibernate-off",
            "Fix_Performance_HibernateOff",
            Id,
            RiskTier.Moderate,
            [new CommandStep("powercfg.exe", ["/hibernate", "off"])],
            async (context, ct) =>
                (await context.Commands.RunAsync("powercfg.exe", ["/a"], TimeSpan.FromMinutes(1), ct)
                    .ConfigureAwait(false)).Success)
    ];

    private static async Task<bool> VerifyCoreServicesAsync(FixContext context, CancellationToken ct)
    {
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "Get-Service Dhcp,Dnscache,NlaSvc,AudioEndpointBuilder,Audiosrv,EventLog,PlugPlay,LanmanWorkstation,Winmgmt | Select-Object Name,Status",
            ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        return rows.Length >= 6 && rows.All(row =>
            string.Equals(ModuleHelpers.GetString(row, "Status"), "Running", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<FixResult> ApplyCoreServicesRepairAsync(
        FixContext context,
        bool restartRunning,
        CancellationToken ct)
    {
        // Startup defaults: Automatic for always-on services, Manual for demand services.
        var script =
            "$ErrorActionPreference='Continue'; $restart=" + (restartRunning ? "$true" : "$false") + "; " +
            "$map=[ordered]@{" +
            "  'Dhcp'='Automatic'; 'Dnscache'='Automatic'; 'NlaSvc'='Automatic'; 'netprofm'='Manual'; 'WlanSvc'='Manual'; " +
            "  'AudioEndpointBuilder'='Automatic'; 'Audiosrv'='Automatic'; " +
            "  'EventLog'='Automatic'; 'PlugPlay'='Automatic'; 'DsmSvc'='Manual'; 'DeviceInstall'='Manual'; " +
            "  'Spooler'='Automatic'; 'BITS'='Manual'; 'cryptsvc'='Automatic'; 'wuauserv'='Manual'; " +
            "  'W32Time'='Manual'; 'bthserv'='Manual'; 'WSearch'='Automatic'; 'LanmanWorkstation'='Automatic'; " +
            "  'Winmgmt'='Automatic'; 'Schedule'='Automatic'; 'Themes'='Automatic'; 'FontCache'='Manual'; " +
            "  'BrokerInfrastructure'='Automatic' " +
            "}; " +
            "foreach($n in $map.Keys){ " +
            "  try{ " +
            "    $svc=Get-Service -Name $n -ErrorAction Stop; " +
            "    $desired=$map[$n]; " +
            "    if($svc.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType $desired -ErrorAction SilentlyContinue }; " +
            "    if($restart -and $svc.Status -eq 'Running'){ Restart-Service -Name $n -Force -ErrorAction SilentlyContinue } " +
            "    elseif($svc.Status -ne 'Running'){ Start-Service -Name $n -ErrorAction SilentlyContinue }; " +
            "    Start-Sleep -Milliseconds 250 " +
            "  }catch{} " +
            "}; exit 0";

        return ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                TimeSpan.FromMinutes(6))
        ], ct);
    }

    private static async Task<BackupEntry?> BackupPowerPlanAsync(FixContext context, CancellationToken ct)
    {
        var result = await context.Commands.RunAsync("powercfg.exe", ["/getactivescheme"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        var match = Regex.Match(result.StdOut, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!result.Success || !match.Success) return null;
        var entry = await context.Backups.CaptureValueAsync("power-plan", new { Guid = match.Value }, ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
        if (entry is not null)
        {
            entry.Metadata["restoreExecutable"] = "powercfg.exe";
            entry.Metadata["restoreArguments"] = JsonSerializer.Serialize(new[] { "/setactive", match.Value });
        }
        return entry;
    }
}

internal static class OtherModuleFunctions
{
    public static string Join(IEnumerable<JsonElement> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => row.ToString()));

    public static bool Bool(JsonElement element, string property) =>
        ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value) &&
        (value.ValueKind == JsonValueKind.True || bool.TryParse(value.ToString(), out var parsed) && parsed);

    public static double Double(JsonElement element, string property) =>
        ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value) &&
        double.TryParse(value.ToString(), out var parsed)
            ? parsed
            : 0;

    public static bool TryMeasureDirectories(
        IEnumerable<string> roots,
        long maximumBytes,
        int maximumEntries,
        out long totalBytes)
    {
        totalBytes = 0;
        var entries = 0;
        var pending = new Stack<string>(roots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath));
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    entries++;
                    if (entries > maximumEntries) return false;
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry);
                        continue;
                    }

                    totalBytes = checked(totalBytes + new FileInfo(entry).Length);
                    if (totalBytes > maximumBytes) return false;
                }
            }

            return true;
        }
        catch
        {
            totalBytes = 0;
            return false;
        }
    }

    public static bool HasFreeSpace(string path, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(root) &&
                   new DriveInfo(root).AvailableFreeSpace >= Math.Max(requiredBytes, 0);
        }
        catch
        {
            return false;
        }
    }
}
