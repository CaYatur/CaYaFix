// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;
using static CaYaFix.Modules.Other.OtherModuleFunctions;

namespace CaYaFix.Modules.Other;

/// <summary>
/// Common Windows restriction-policy breakage: Task Manager / Registry Editor /
/// Command Prompt / Control Panel blocked, Windows Update policy locks, and a
/// documented local Group Policy reset path.
/// </summary>
public sealed class SystemPolicyModule : WindowsModuleBase
{
    private const string Id = "policy";

    private const string PoliciesSystemCu = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PoliciesSystemLm = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string WindowsPolicySystemCu = @"HKCU\Software\Policies\Microsoft\Windows\System";
    private const string WindowsPolicySystemLm = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\System";
    private const string PoliciesExplorerCu = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string PoliciesExplorerLm = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    private const string WindowsUpdatePolicy = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    public SystemPolicyModule() : base(
        new ModuleInfo(Id, "Module_Policy_Name", "Module_Policy_Description", "policy.svg", 17),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("policy.blocked", Id, "Symptom_Policy_Blocked",
                ["policy.taskmgr", "policy.regedit", "policy.cmd", "policy.control-panel"],
                ["policy.enable-taskmgr", "policy.enable-regedit", "policy.enable-cmd", "policy.enable-control-panel", "policy.gpupdate-force"]),
            new Playbook("policy.update-locked", Id, "Symptom_Policy_UpdateLocked",
                ["policy.update-locks"],
                ["policy.unlock-windows-update", "policy.gpupdate-force"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("policy.taskmgr", "Check_Policy_TaskManager", Id, (context, ct) =>
            ProbeRestrictionValueAsync(
                context, "policy.taskmgr", "Finding_Policy_TaskManagerBlocked", "DisableTaskMgr",
                [PoliciesSystemCu, PoliciesSystemLm],
                ["policy.enable-taskmgr", "policy.gpupdate-force"], ct)),
        new DelegateDiagnosticCheck("policy.regedit", "Check_Policy_Registry", Id, (context, ct) =>
            ProbeRestrictionValueAsync(
                context, "policy.regedit", "Finding_Policy_RegistryBlocked", "DisableRegistryTools",
                [PoliciesSystemCu, PoliciesSystemLm],
                ["policy.enable-regedit", "policy.gpupdate-force"], ct)),
        new DelegateDiagnosticCheck("policy.cmd", "Check_Policy_Cmd", Id, (context, ct) =>
            ProbeRestrictionValueAsync(
                context, "policy.cmd", "Finding_Policy_CmdBlocked", "DisableCMD",
                [WindowsPolicySystemCu, WindowsPolicySystemLm],
                ["policy.enable-cmd", "policy.gpupdate-force"], ct)),
        new DelegateDiagnosticCheck("policy.control-panel", "Check_Policy_ControlPanel", Id, (context, ct) =>
            ProbeRestrictionValueAsync(
                context, "policy.control-panel", "Finding_Policy_ControlPanelBlocked", "NoControlPanel",
                [PoliciesExplorerCu, PoliciesExplorerLm],
                ["policy.enable-control-panel", "policy.gpupdate-force"], ct)),
        new DelegateDiagnosticCheck("policy.update-locks", "Check_Policy_UpdateLocks", Id, async (context, ct) =>
        {
            const string script =
                "$found=@(); " +
                "$wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; " +
                "if(Test-Path $wu){ $p=Get-ItemProperty -Path $wu -ErrorAction SilentlyContinue; " +
                "  foreach($n in @('DisableWindowsUpdateAccess','SetDisableUXWUAccess')){ " +
                "    $v=$p.$n; if($null -ne $v -and [int]$v -ne 0){ $found += \"WindowsUpdate\\$n=$v\" } } }; " +
                "$au=Join-Path $wu 'AU'; " +
                "if(Test-Path $au){ $p=Get-ItemProperty -Path $au -ErrorAction SilentlyContinue; " +
                "  $v=$p.NoAutoUpdate; if($null -ne $v -and [int]$v -ne 0){ $found += \"WindowsUpdate\\AU\\NoAutoUpdate=$v\" } }; " +
                "[pscustomobject]@{Found=@($found)}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var entries = ReadStringArray(ModuleHelpers.Array(root).FirstOrDefault(), "Found");
            return entries.Length > 0
                ? ModuleHelpers.Finding("policy.update-locks", Id, Severity.Warning, "Finding_Policy_UpdateLocked",
                    string.Join(Environment.NewLine, entries),
                    "policy.unlock-windows-update", "policy.gpupdate-force")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("policy.enable-taskmgr", "Fix_Policy_EnableTaskManager", Id, RiskTier.Moderate,
            (context, ct) => BackupPolicyKeysAsync(context, "policy-taskmgr", [PoliciesSystemCu, PoliciesSystemLm], ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                DeleteValueSteps("DisableTaskMgr", PoliciesSystemCu, PoliciesSystemLm), ct),
            (context, ct) => VerifyRestrictionClearedAsync(context, "DisableTaskMgr", [PoliciesSystemCu, PoliciesSystemLm], ct)),
        new DelegateFixAction("policy.enable-regedit", "Fix_Policy_EnableRegistry", Id, RiskTier.Moderate,
            (context, ct) => BackupPolicyKeysAsync(context, "policy-regedit", [PoliciesSystemCu, PoliciesSystemLm], ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                DeleteValueSteps("DisableRegistryTools", PoliciesSystemCu, PoliciesSystemLm), ct),
            (context, ct) => VerifyRestrictionClearedAsync(context, "DisableRegistryTools", [PoliciesSystemCu, PoliciesSystemLm], ct)),
        new DelegateFixAction("policy.enable-cmd", "Fix_Policy_EnableCmd", Id, RiskTier.Moderate,
            (context, ct) => BackupPolicyKeysAsync(context, "policy-cmd", [WindowsPolicySystemCu, WindowsPolicySystemLm], ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                DeleteValueSteps("DisableCMD", WindowsPolicySystemCu, WindowsPolicySystemLm), ct),
            (context, ct) => VerifyRestrictionClearedAsync(context, "DisableCMD", [WindowsPolicySystemCu, WindowsPolicySystemLm], ct)),
        new DelegateFixAction("policy.enable-control-panel", "Fix_Policy_EnableControlPanel", Id, RiskTier.Moderate,
            (context, ct) => BackupPolicyKeysAsync(context, "policy-control-panel", [PoliciesExplorerCu, PoliciesExplorerLm], ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                DeleteValueSteps("NoControlPanel", PoliciesExplorerCu, PoliciesExplorerLm), ct),
            (context, ct) => VerifyRestrictionClearedAsync(context, "NoControlPanel", [PoliciesExplorerCu, PoliciesExplorerLm], ct)),
        new DelegateFixAction("policy.unlock-windows-update", "Fix_Policy_UnlockWindowsUpdate", Id, RiskTier.Moderate,
            (context, ct) => BackupPolicyKeysAsync(context, "policy-update-locks", [WindowsUpdatePolicy], ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("reg.exe", ["delete", WindowsUpdatePolicy, "/v", "DisableWindowsUpdateAccess", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", WindowsUpdatePolicy, "/v", "SetDisableUXWUAccess", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", WindowsUpdatePolicy + @"\AU", "/v", "NoAutoUpdate", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } }
            ], ct),
            async (context, ct) =>
            {
                const string script =
                    "$bad=0; $wu='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'; " +
                    "if(Test-Path $wu){ $p=Get-ItemProperty -Path $wu -ErrorAction SilentlyContinue; " +
                    "  foreach($n in @('DisableWindowsUpdateAccess','SetDisableUXWUAccess')){ " +
                    "    if($null -ne $p.$n -and [int]($p.$n) -ne 0){ $bad++ } } }; " +
                    "$au=Join-Path $wu 'AU'; " +
                    "if(Test-Path $au){ $p=Get-ItemProperty -Path $au -ErrorAction SilentlyContinue; " +
                    "  if($null -ne $p.NoAutoUpdate -and [int]($p.NoAutoUpdate) -ne 0){ $bad++ } }; " +
                    "[pscustomobject]@{Bad=$bad}";
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Double(row, "Bad") == 0);
            }),
        ExpandedRepairHelpers.TransientCommand(
            "policy.gpupdate-force",
            "Fix_Policy_GpUpdate",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("gpupdate.exe", ["/force", "/wait:180"], TimeSpan.FromMinutes(8))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync("gpupdate.exe", ["/target:computer", "/wait:60"], TimeSpan.FromMinutes(4), ct)
                    .ConfigureAwait(false)).Success),
        new DelegateFixAction("policy.reset-local-gpo", "Fix_Policy_ResetLocalGpo", Id, RiskTier.Aggressive,
            BackupLocalGpoAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "$gp=Join-Path $env:windir 'System32\\GroupPolicy'; " +
                        "foreach($sub in @('Machine','User')){ " +
                        "  $pol=Join-Path (Join-Path $gp $sub) 'Registry.pol'; " +
                        "  if(Test-Path -LiteralPath $pol){ Remove-Item -LiteralPath $pol -Force -ErrorAction SilentlyContinue } }; exit 0"
                    ],
                    TimeSpan.FromMinutes(3)),
                new CommandStep("gpupdate.exe", ["/force", "/wait:180"], TimeSpan.FromMinutes(8))
            ], ct),
            async (context, ct) =>
                (await context.Commands.RunAsync("gpupdate.exe", ["/target:computer", "/wait:60"], TimeSpan.FromMinutes(4), ct)
                    .ConfigureAwait(false)).Success)
    ];

    private static async Task<Finding?> ProbeRestrictionValueAsync(
        DiagnosticContext context,
        string checkId,
        string findingKey,
        string valueName,
        string[] registryKeys,
        string[] fixIds,
        CancellationToken ct)
    {
        var script = BuildRestrictionProbeScript(valueName, registryKeys);
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var entries = ReadStringArray(ModuleHelpers.Array(root).FirstOrDefault(), "Found");
        return entries.Length > 0
            ? ModuleHelpers.Finding(checkId, Id, Severity.Warning, findingKey,
                string.Join(Environment.NewLine, entries), fixIds)
            : null;
    }

    private static async Task<bool> VerifyRestrictionClearedAsync(
        FixContext context,
        string valueName,
        string[] registryKeys,
        CancellationToken ct)
    {
        var script = BuildRestrictionProbeScript(valueName, registryKeys);
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var row = ModuleHelpers.Array(root).FirstOrDefault();
        return row.ValueKind == JsonValueKind.Object &&
               ReadStringArray(row, "Found").Length == 0;
    }

    private static string BuildRestrictionProbeScript(string valueName, string[] registryKeys)
    {
        var paths = string.Join("','", registryKeys.Select(ToPowerShellRegistryPath));
        return
            "$found=@(); foreach($k in @('" + paths + "')){ " +
            "  if(Test-Path $k){ $p=Get-ItemProperty -Path $k -ErrorAction SilentlyContinue; " +
            "    $v=$p.'" + valueName + "'; if($null -ne $v -and [int]$v -ne 0){ $found += \"$k\\" + valueName + "=$v\" } } }; " +
            "[pscustomobject]@{Found=@($found)}";
    }

    private static string ToPowerShellRegistryPath(string key) =>
        key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
            ? "HKCU:" + key[4..]
            : "HKLM:" + key[4..];

    private static CommandStep[] DeleteValueSteps(string valueName, params string[] registryKeys) =>
        registryKeys
            .Select(key => new CommandStep("reg.exe", ["delete", key, "/v", valueName, "/f"])
            {
                AcceptedExitCodes = new HashSet<int> { 0, 1 }
            })
            .ToArray();

    private static async Task<BackupEntry?> BackupPolicyKeysAsync(
        FixContext context,
        string label,
        string[] registryKeys,
        CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var entries = new List<BackupEntry>();
        foreach (var key in registryKeys)
        {
            var entry = await ModuleHelpers.CaptureRegistryOrMarkerAsync(context, key, $"{label}-marker", ct)
                .ConfigureAwait(false);
            if (entry is null) return null;
            entries.Add(entry);
        }

        return entries.Count == 1
            ? entries[0]
            : await context.Backups.CaptureBundleAsync(label, entries, dir, ct).ConfigureAwait(false);
    }

    private static async Task<BackupEntry?> BackupLocalGpoAsync(FixContext context, CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var groupPolicy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "GroupPolicy");
        var entries = new List<BackupEntry>();
        foreach (var scope in new[] { "Machine", "User" })
        {
            var policyFile = Path.Combine(groupPolicy, scope, "Registry.pol");
            if (!File.Exists(policyFile)) continue;
            var entry = await context.Backups.CaptureFileAsync(policyFile, dir, ct).ConfigureAwait(false);
            if (entry is null) return null;
            entries.Add(entry);
        }

        return entries.Count switch
        {
            0 => await ModuleHelpers.TransientMarkerAsync(context, "policy-local-gpo-empty", ct).ConfigureAwait(false),
            1 => entries[0],
            _ => await context.Backups.CaptureBundleAsync("policy-local-gpo", entries, dir, ct).ConfigureAwait(false)
        };
    }

    private static string[] ReadStringArray(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !ModuleHelpers.TryGetPropertyIgnoreCase(row, property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(item => item.ToString()).ToArray(),
            JsonValueKind.String => [value.ToString()],
            _ => []
        };
    }
}

/// <summary>
/// System plumbing many features quietly depend on: the WMI service and repository,
/// the Windows Event Log, and performance counters.
/// </summary>
public sealed class WmiRepairModule : WindowsModuleBase
{
    private const string Id = "wmi";

    public WmiRepairModule() : base(
        new ModuleInfo(Id, "Module_Wmi_Name", "Module_Wmi_Description", "wmi.svg", 18),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("wmi.broken", Id, "Symptom_Wmi_Broken",
                ["wmi.service", "wmi.repository", "wmi.perf-counters", "wmi.eventlog"],
                ["wmi.verify-repository", "wmi.restart-winmgmt", "wmi.resync-perf", "wmi.restart-eventlog"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("wmi.service", "Check_Wmi_Service", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                "Get-CimInstance Win32_Service -Filter \"Name='Winmgmt'\" | Select-Object Name,State,StartMode",
                ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var unhealthy = rows.Length != 1 || rows.Any(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("wmi.service", Id, Severity.Critical, "Finding_Wmi_ServiceIssue", Join(rows),
                    "wmi.restart-winmgmt", "wmi.verify-repository")
                : null;
        }),
        new DelegateDiagnosticCheck("wmi.repository", "Check_Wmi_Repository", Id, async (context, ct) =>
        {
            var result = await context.Commands.RunAsync(
                "winmgmt.exe", ["/verifyrepository"], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            var state = SystemCommandResultParsers.ParseWmiRepositoryState(
                result.StdOut, result.StdErr, result.ExitCode);
            return state == SystemCommandResultParsers.WmiRepositoryState.Inconsistent
                ? ModuleHelpers.Finding("wmi.repository", Id, Severity.Critical, "Finding_Wmi_RepositoryInconsistent",
                    (result.StdOut + Environment.NewLine + result.StdErr).Trim(),
                    "wmi.salvage-repository", "wmi.restart-winmgmt")
                : null;
        }),
        new DelegateDiagnosticCheck("wmi.eventlog", "Check_Wmi_EventLog", Id, async (context, ct) =>
        {
            const string script =
                "$svc=Get-CimInstance Win32_Service -Filter \"Name='EventLog'\" | Select-Object Name,State,StartMode; " +
                "$full=@(); try{ $full=@(Get-WinEvent -ListLog 'System','Application' -ErrorAction SilentlyContinue | " +
                "  Where-Object { $_.IsLogFull } | Select-Object -ExpandProperty LogName) }catch{}; " +
                "[pscustomobject]@{Name=$svc.Name;State=[string]$svc.State;StartMode=[string]$svc.StartMode;FullLogs=@($full)}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object) return null;
            var stopped = !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase);
            var hasFullLogs = ModuleHelpers.TryGetPropertyIgnoreCase(row, "FullLogs", out var full) &&
                              full.ValueKind == JsonValueKind.Array && full.GetArrayLength() > 0;
            return stopped || hasFullLogs
                ? ModuleHelpers.Finding("wmi.eventlog", Id, stopped ? Severity.Critical : Severity.Warning,
                    "Finding_Wmi_EventLogIssue", row.ToString(), "wmi.restart-eventlog")
                : null;
        }),
        new DelegateDiagnosticCheck("wmi.perf-counters", "Check_Wmi_PerfCounters", Id, async (context, ct) =>
        {
            // Counter path names are localized, so probe by enumerating counter sets
            // (locale-independent) instead of sampling an English-only counter path.
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(PerfCounterProbeScript, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            return row.ValueKind == JsonValueKind.Object && Double(row, "Sets") == 0
                ? ModuleHelpers.Finding("wmi.perf-counters", Id, Severity.Warning, "Finding_Wmi_PerfCountersBroken", row.ToString(),
                    "wmi.resync-perf", "wmi.rebuild-perfcounters")
                : null;
        }, quick: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("wmi.restart-winmgmt", "Fix_Wmi_RestartWinmgmt", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureServicesAsync(["Winmgmt"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "try{ Set-Service Winmgmt -StartupType Automatic -ErrorAction SilentlyContinue }catch{}; " +
                        "try{ Restart-Service Winmgmt -Force -ErrorAction Stop }catch{ Start-Service Winmgmt -ErrorAction SilentlyContinue }; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "Winmgmt", ct)),
        ExpandedRepairHelpers.TransientCommand(
            "wmi.verify-repository",
            "Fix_Wmi_VerifyRepository",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("winmgmt.exe", ["/verifyrepository"], TimeSpan.FromMinutes(5))
            ],
            VerifyWmiRepositoryConsistentAsync),
        new DelegateFixAction("wmi.salvage-repository", "Fix_Wmi_SalvageRepository", Id, RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "wmi-salvage-repository", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("winmgmt.exe", ["/salvagerepository"], TimeSpan.FromMinutes(15)),
                new CommandStep("winmgmt.exe", ["/verifyrepository"], TimeSpan.FromMinutes(5))
            ], ct),
            VerifyWmiRepositoryConsistentAsync,
            requiresReboot: true),
        new DelegateFixAction("wmi.restart-eventlog", "Fix_Wmi_RestartEventLog", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureServicesAsync(["EventLog"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "try{ Set-Service EventLog -StartupType Automatic -ErrorAction SilentlyContinue }catch{}; " +
                        "try{ Restart-Service EventLog -Force -ErrorAction Stop }catch{ Start-Service EventLog -ErrorAction SilentlyContinue }; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "EventLog", ct)),
        new DelegateFixAction("wmi.rebuild-perfcounters", "Fix_Wmi_RebuildPerfCounters", Id, RiskTier.Aggressive,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "wmi-rebuild-perfcounters", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("lodctr.exe", ["/R"], TimeSpan.FromMinutes(10)),
                new CommandStep("winmgmt.exe", ["/resyncperf"], TimeSpan.FromMinutes(5))
            ], ct),
            VerifyPerfCountersAsync),
        ExpandedRepairHelpers.TransientCommand(
            "wmi.resync-perf",
            "Fix_Wmi_ResyncPerf",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("winmgmt.exe", ["/resyncperf"], TimeSpan.FromMinutes(5))
            ],
            VerifyPerfCountersAsync)
    ];

    private static async Task<bool> VerifyWmiRepositoryConsistentAsync(FixContext context, CancellationToken ct)
    {
        var result = await context.Commands.RunAsync(
            "winmgmt.exe", ["/verifyrepository"], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        return SystemCommandResultParsers.ParseWmiRepositoryState(result.StdOut, result.StdErr, result.ExitCode)
               == SystemCommandResultParsers.WmiRepositoryState.Consistent;
    }

    private const string PerfCounterProbeScript =
        "$sets=0; $err=''; " +
        "try{ $sets=@(Get-Counter -ListSet * -ErrorAction Stop).Count }catch{ $err=[string]$_.Exception.Message }; " +
        "[pscustomobject]@{Sets=$sets;Error=$err}";

    private static async Task<bool> VerifyPerfCountersAsync(FixContext context, CancellationToken ct)
    {
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(PerfCounterProbeScript, ct).ConfigureAwait(false);
        return ModuleHelpers.Array(root).Any(row => Double(row, "Sets") > 0);
    }
}
