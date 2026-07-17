// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;
using static CaYaFix.Modules.Other.OtherModuleFunctions;

namespace CaYaFix.Modules.Other;

/// <summary>
/// Windows Security (Defender, real-time protection, signatures, firewall, Security Center).
/// Findings stay quiet when a healthy third-party antivirus legitimately owns protection.
/// </summary>
public sealed class WindowsSecurityModule : WindowsModuleBase
{
    private const string Id = "defender";

    public WindowsSecurityModule() : base(
        new ModuleInfo(Id, "Module_Defender_Name", "Module_Defender_Description", "defender.svg", 15),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("defender.protection-off", Id, "Symptom_Defender_Off",
                ["defender.services", "defender.realtime", "defender.policy-block", "defender.signatures"],
                ["defender.clear-policy-block", "defender.start-services", "defender.enable-realtime", "defender.update-signatures"]),
            new Playbook("defender.firewall-issues", Id, "Symptom_Defender_Firewall",
                ["defender.firewall"],
                ["defender.enable-firewall", "defender.restart-security-center"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("defender.services", "Check_Defender_Services", Id, async (context, ct) =>
        {
            const string script =
                "$svc=@(Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('WinDefend','WdNisSvc','SecurityHealthService','wscsvc')} | Select-Object Name,State,StartMode); " +
                "$third=$false; try{ $av=@(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop); " +
                "  $third=@($av | Where-Object { [string]$_.displayName -notmatch 'Defender|Windows' }).Count -gt 0 }catch{}; " +
                "[pscustomobject]@{Services=@($svc);ThirdPartyAv=$third}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object || Bool(row, "ThirdPartyAv")) return null;
            if (!ModuleHelpers.TryGetPropertyIgnoreCase(row, "Services", out var services)) return null;
            var rows = services.ValueKind == JsonValueKind.Array
                ? services.EnumerateArray().ToArray()
                : services.ValueKind == JsonValueKind.Object ? [services] : [];
            var securityCenter = rows.FirstOrDefault(item =>
                string.Equals(ModuleHelpers.GetString(item, "Name"), "wscsvc", StringComparison.OrdinalIgnoreCase));
            var unhealthy =
                rows.Any(item => string.Equals(ModuleHelpers.GetString(item, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)) ||
                securityCenter.ValueKind != JsonValueKind.Object ||
                !string.Equals(ModuleHelpers.GetString(securityCenter, "State"), "Running", StringComparison.OrdinalIgnoreCase);
            return unhealthy
                ? ModuleHelpers.Finding("defender.services", Id, Severity.Warning, "Finding_Defender_ServicesUnhealthy", Join(rows),
                    "defender.start-services", "defender.clear-policy-block", "defender.restart-security-center")
                : null;
        }),
        new DelegateDiagnosticCheck("defender.realtime", "Check_Defender_Realtime", Id, async (context, ct) =>
        {
            const string script =
                "$third=$false; try{ $av=@(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop); " +
                "  $third=@($av | Where-Object { [string]$_.displayName -notmatch 'Defender|Windows' }).Count -gt 0 }catch{}; " +
                "$status=$null; try{ $status=Get-MpComputerStatus -ErrorAction Stop }catch{}; " +
                "if($null -eq $status){ [pscustomobject]@{Available=$false;ThirdPartyAv=$third} } else { " +
                "[pscustomobject]@{Available=$true;ThirdPartyAv=$third;RealTime=[bool]$status.RealTimeProtectionEnabled;Antivirus=[bool]$status.AntivirusEnabled;Mode=[string]$status.AMRunningMode} }";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object || Bool(row, "ThirdPartyAv") || !Bool(row, "Available")) return null;
            return !Bool(row, "RealTime") || !Bool(row, "Antivirus")
                ? ModuleHelpers.Finding("defender.realtime", Id, Severity.Critical, "Finding_Defender_RealtimeOff", row.ToString(),
                    "defender.clear-policy-block", "defender.enable-realtime", "defender.start-services")
                : null;
        }),
        new DelegateDiagnosticCheck("defender.signatures", "Check_Defender_Signatures", Id, async (context, ct) =>
        {
            const string script =
                "$third=$false; try{ $av=@(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop); " +
                "  $third=@($av | Where-Object { [string]$_.displayName -notmatch 'Defender|Windows' }).Count -gt 0 }catch{}; " +
                "$status=$null; try{ $status=Get-MpComputerStatus -ErrorAction Stop }catch{}; " +
                "if($null -eq $status){ [pscustomobject]@{Available=$false;ThirdPartyAv=$third} } else { " +
                "[pscustomobject]@{Available=$true;ThirdPartyAv=$third;SignatureAgeDays=[int]$status.AntivirusSignatureAge;LastUpdated=[string]$status.AntivirusSignatureLastUpdated} }";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object || Bool(row, "ThirdPartyAv") || !Bool(row, "Available")) return null;
            var maximumAge = context.Thresholds.Get("defender.signatureAgeWarningDays", 7);
            return Double(row, "SignatureAgeDays") > maximumAge
                ? ModuleHelpers.Finding("defender.signatures", Id, Severity.Warning, "Finding_Defender_SignaturesOld", row.ToString(),
                    "defender.update-signatures", "defender.start-services")
                : null;
        }),
        new DelegateDiagnosticCheck("defender.firewall", "Check_Defender_Firewall", Id, async (context, ct) =>
        {
            // Enabled is a GpoBoolean enum that serializes as 0/1 — cast to a real boolean in PS.
            const string script = "Get-NetFirewallProfile -ErrorAction Stop | Select-Object Name,@{n='Enabled';e={[bool]$_.Enabled}}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var disabled = ModuleHelpers.Array(root).Where(row => !Bool(row, "Enabled")).ToArray();
            return disabled.Length > 0
                ? ModuleHelpers.Finding("defender.firewall", Id, Severity.Critical, "Finding_Defender_FirewallOff", Join(disabled),
                    "defender.enable-firewall", "defender.restart-security-center")
                : null;
        }),
        new DelegateDiagnosticCheck("defender.policy-block", "Check_Defender_PolicyBlock", Id, async (context, ct) =>
        {
            const string script =
                "$result=@(); " +
                "$rootKey='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows Defender'; " +
                "if(Test-Path $rootKey){ $p=Get-ItemProperty -Path $rootKey -ErrorAction SilentlyContinue; " +
                "  foreach($n in @('DisableAntiSpyware','DisableAntiVirus')){ $v=$p.$n; if($null -ne $v -and [int]$v -ne 0){ $result += \"$n=$v\" } } }; " +
                "$rtKey=Join-Path $rootKey 'Real-Time Protection'; " +
                "if(Test-Path $rtKey){ $p=Get-ItemProperty -Path $rtKey -ErrorAction SilentlyContinue; " +
                "  foreach($n in @('DisableRealtimeMonitoring','DisableBehaviorMonitoring','DisableOnAccessProtection','DisableScanOnRealtimeEnable')){ " +
                "    $v=$p.$n; if($null -ne $v -and [int]$v -ne 0){ $result += \"RealTimeProtection\\$n=$v\" } } }; " +
                "[pscustomobject]@{Blocked=@($result)}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object ||
                !ModuleHelpers.TryGetPropertyIgnoreCase(row, "Blocked", out var blocked))
            {
                return null;
            }

            var entries = blocked.ValueKind switch
            {
                JsonValueKind.Array => blocked.EnumerateArray().Select(item => item.ToString()).ToArray(),
                JsonValueKind.String => [blocked.ToString()],
                _ => []
            };
            return entries.Length > 0
                ? ModuleHelpers.Finding("defender.policy-block", Id, Severity.Critical, "Finding_Defender_PolicyBlocked",
                    string.Join(Environment.NewLine, entries),
                    "defender.clear-policy-block", "defender.start-services")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("defender.start-services", "Fix_Defender_StartServices", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["wscsvc", "SecurityHealthService"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "foreach($n in @('wscsvc','SecurityHealthService','WinDefend','WdNisSvc')){ " +
                        "  try{ $s=Get-Service -Name $n -ErrorAction Stop; " +
                        "    if($s.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType Manual -ErrorAction SilentlyContinue }; " +
                        "    if($s.Status -ne 'Running'){ Start-Service -Name $n -ErrorAction SilentlyContinue } }catch{} " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(4))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "wscsvc", ct)),
        new DelegateFixAction("defender.enable-realtime", "Fix_Defender_EnableRealtime", Id, RiskTier.Moderate,
            BackupRealtimePreferenceAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop"],
                    TimeSpan.FromMinutes(3))
            ], ct),
            async (context, ct) =>
            {
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    "try{ $s=Get-MpComputerStatus -ErrorAction Stop; [pscustomobject]@{RealTime=[bool]$s.RealTimeProtectionEnabled} }catch{ [pscustomobject]@{RealTime=$false} }",
                    ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Bool(row, "RealTime"));
            }),
        ExpandedRepairHelpers.TransientCommand(
            "defender.update-signatures",
            "Fix_Defender_UpdateSignatures",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "try{ Update-MpSignature -ErrorAction Stop; exit 0 }catch{ exit 1 }"],
                    TimeSpan.FromMinutes(20))
            ],
            async (context, ct) =>
            {
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    "try{ $s=Get-MpComputerStatus -ErrorAction Stop; [pscustomobject]@{Age=[int]$s.AntivirusSignatureAge} }catch{ [pscustomobject]@{Age=999} }",
                    ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Double(row, "Age") <= 7);
            }),
        ExpandedRepairHelpers.TransientCommand(
            "defender.quick-scan",
            "Fix_Defender_QuickScan",
            Id,
            RiskTier.Safe,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "try{ Start-MpScan -ScanType QuickScan -ErrorAction Stop; exit 0 }catch{ exit 1 }"],
                    TimeSpan.FromMinutes(60))
            ],
            async (_, _) => await Task.FromResult(true)),
        new DelegateFixAction("defender.enable-firewall", "Fix_Defender_EnableFirewall", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureRegistryAsync(
                @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy",
                ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("netsh.exe", ["advfirewall", "set", "allprofiles", "state", "on"], TimeSpan.FromMinutes(2))], ct),
            async (context, ct) =>
            {
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    "Get-NetFirewallProfile -ErrorAction Stop | Select-Object Name,@{n='Enabled';e={[bool]$_.Enabled}}",
                    ct).ConfigureAwait(false);
                var rows = ModuleHelpers.Array(root).ToArray();
                return rows.Length > 0 && rows.All(row => Bool(row, "Enabled"));
            }),
        new DelegateFixAction("defender.clear-policy-block", "Fix_Defender_ClearPolicyBlock", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.CaptureRegistryOrMarkerAsync(
                context, @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "defender-policy-block", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "/v", "DisableAntiSpyware", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "/v", "DisableAntiVirus", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "/v", "DisableRealtimeMonitoring", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "/v", "DisableBehaviorMonitoring", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "/v", "DisableOnAccessProtection", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "/v", "DisableScanOnRealtimeEnable", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } }
            ], ct),
            async (context, ct) =>
            {
                const string script =
                    "$bad=0; $rootKey='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows Defender'; " +
                    "if(Test-Path $rootKey){ $p=Get-ItemProperty -Path $rootKey -ErrorAction SilentlyContinue; " +
                    "  foreach($n in @('DisableAntiSpyware','DisableAntiVirus')){ if($null -ne $p.$n -and [int]($p.$n) -ne 0){ $bad++ } } }; " +
                    "$rtKey=Join-Path $rootKey 'Real-Time Protection'; " +
                    "if(Test-Path $rtKey){ $p=Get-ItemProperty -Path $rtKey -ErrorAction SilentlyContinue; " +
                    "  foreach($n in @('DisableRealtimeMonitoring','DisableBehaviorMonitoring','DisableOnAccessProtection','DisableScanOnRealtimeEnable')){ " +
                    "    if($null -ne $p.$n -and [int]($p.$n) -ne 0){ $bad++ } } }; " +
                    "[pscustomobject]@{Bad=$bad}";
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Double(row, "Bad") == 0);
            }),
        ExpandedRepairHelpers.RestartNamedServices(
            "defender.restart-security-center", "Fix_Defender_RestartSecurityCenter", Id,
            ["wscsvc", "SecurityHealthService"], "wscsvc")
    ];

    private static async Task<BackupEntry?> BackupRealtimePreferenceAsync(FixContext context, CancellationToken ct)
    {
        bool currentDisabled;
        try
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                "try{ $p=Get-MpPreference -ErrorAction Stop; [pscustomobject]@{Disabled=[bool]$p.DisableRealtimeMonitoring} }catch{ [pscustomobject]@{Disabled=$false} }",
                ct).ConfigureAwait(false);
            currentDisabled = ModuleHelpers.Array(root).Any(row => Bool(row, "Disabled"));
        }
        catch
        {
            currentDisabled = false;
        }

        var entry = await context.Backups.CaptureValueAsync(
            "defender-realtime-preference",
            new { DisableRealtimeMonitoring = currentDisabled },
            ModuleHelpers.BackupDirectory(context),
            ct).ConfigureAwait(false);
        if (entry is not null)
        {
            var literal = currentDisabled ? "$true" : "$false";
            entry.Metadata["restoreExecutable"] = "powershell.exe";
            entry.Metadata["restoreArguments"] = JsonSerializer.Serialize(new[]
            {
                "-NoProfile", "-NonInteractive", "-Command",
                $"Set-MpPreference -DisableRealtimeMonitoring {literal} -ErrorAction Stop"
            });
        }

        return entry;
    }
}

/// <summary>
/// File Explorer, desktop shell, icon/thumbnail/font caches, user shell folders,
/// jump lists, and folder view state.
/// </summary>
public sealed class ExplorerShellModule : WindowsModuleBase
{
    private const string Id = "shell";

    public ExplorerShellModule() : base(
        new ModuleInfo(Id, "Module_Shell_Name", "Module_Shell_Description", "shell.svg", 16),
        CreateChecks(), CreateFixes(),
        [
            new Playbook("shell.unstable", Id, "Symptom_Shell_Unstable",
                ["shell.explorer", "shell.crashes", "shell.services"],
                ["shell.restart-explorer", "shell.restart-shell-services", "shell.rebuild-icon-cache"]),
            new Playbook("shell.icons-broken", Id, "Symptom_Shell_IconsBroken",
                ["shell.cache", "shell.user-folders"],
                ["shell.rebuild-icon-cache", "shell.clear-thumbnails", "shell.rebuild-font-cache", "shell.fix-user-folders"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("shell.explorer", "Check_Shell_ExplorerRunning", Id, async (context, ct) =>
        {
            const string script = "[pscustomobject]@{Count=@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            return row.ValueKind == JsonValueKind.Object && Double(row, "Count") == 0
                ? ModuleHelpers.Finding("shell.explorer", Id, Severity.Critical, "Finding_Shell_ExplorerNotRunning", row.ToString(),
                    "shell.restart-explorer")
                : null;
        }),
        new DelegateDiagnosticCheck("shell.cache", "Check_Shell_IconCache", Id, async (context, ct) =>
        {
            const string script =
                "$dir=Join-Path $env:LOCALAPPDATA 'Microsoft\\Windows\\Explorer'; $bytes=0; $files=0; " +
                "if(Test-Path -LiteralPath $dir){ Get-ChildItem -LiteralPath $dir -Filter '*.db' -File -Force -ErrorAction SilentlyContinue | " +
                "  Where-Object { $_.Name -match 'iconcache|thumbcache' } | ForEach-Object { $bytes += $_.Length; $files++ } }; " +
                "[pscustomobject]@{Files=$files;Mb=[math]::Round($bytes/1MB,1)}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object) return null;
            var warningMb = context.Thresholds.Get("shell.cacheWarningMb", 750);
            return Double(row, "Mb") > warningMb
                ? ModuleHelpers.Finding("shell.cache", Id, Severity.Info, "Finding_Shell_CacheHeavy", row.ToString(),
                    "shell.rebuild-icon-cache", "shell.clear-thumbnails")
                : null;
        }),
        new DelegateDiagnosticCheck("shell.crashes", "Check_Shell_Crashes", Id, async (context, ct) =>
        {
            const string script =
                "$since=(Get-Date).AddDays(-7); " +
                "Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='Application Error';Id=1000;StartTime=$since} -MaxEvents 40 -ErrorAction SilentlyContinue | " +
                "Where-Object { [string]$_.Message -match 'explorer' } | Select-Object -First 8 TimeCreated,Id,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding("shell.crashes", Id, Severity.Warning, "Finding_Shell_RecentCrashes", Join(rows),
                    "shell.restart-explorer", "shell.rebuild-icon-cache", "shell.reset-folder-views")
                : null;
        }, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("shell.user-folders", "Check_Shell_UserFolders", Id, async (context, ct) =>
        {
            const string script =
                "$key='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders'; $missing=@(); " +
                "if(Test-Path $key){ $p=Get-Item -Path $key; foreach($n in $p.GetValueNames()){ " +
                "  $raw=[string]$p.GetValue($n,$null,'DoNotExpandEnvironmentNames'); if([string]::IsNullOrWhiteSpace($raw)){ continue }; " +
                "  $expanded=[Environment]::ExpandEnvironmentVariables($raw); " +
                "  if($expanded -notmatch '^[A-Za-z]:\\\\'){ continue }; " +
                "  if(-not (Test-Path -LiteralPath $expanded)){ $missing += \"$n=$expanded\" } } }; " +
                "[pscustomobject]@{Missing=@($missing)}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object ||
                !ModuleHelpers.TryGetPropertyIgnoreCase(row, "Missing", out var missing))
            {
                return null;
            }

            var entries = missing.ValueKind switch
            {
                JsonValueKind.Array => missing.EnumerateArray().Select(item => item.ToString()).ToArray(),
                JsonValueKind.String => [missing.ToString()],
                _ => []
            };
            return entries.Length > 0
                ? ModuleHelpers.Finding("shell.user-folders", Id, Severity.Warning, "Finding_Shell_UserFolderMissing",
                    string.Join(Environment.NewLine, entries), "shell.fix-user-folders")
                : null;
        }),
        new DelegateDiagnosticCheck("shell.services", "Check_Shell_Services", Id, async (context, ct) =>
        {
            const string script = "Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('Themes','ShellHWDetection','FontCache')} | Select-Object Name,State,StartMode";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var bad = rows.Where(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ModuleHelpers.GetString(row, "Name"), "Themes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase)).ToArray();
            return bad.Length > 0
                ? ModuleHelpers.Finding("shell.services", Id, Severity.Warning, "Finding_Shell_ServicesUnhealthy", Join(bad),
                    "shell.restart-shell-services")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("shell.restart-explorer", "Fix_Shell_RestartExplorer", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "shell-restart-explorer", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                        "if(@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count -eq 0){ Start-Process explorer }; " +
                        "Start-Sleep -Seconds 2; exit 0"
                    ],
                    TimeSpan.FromMinutes(3))
            ], ct),
            VerifyExplorerRunningAsync),
        new DelegateFixAction("shell.rebuild-icon-cache", "Fix_Shell_RebuildIconCache", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "shell-icon-cache", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                        "$legacy=Join-Path $env:LOCALAPPDATA 'IconCache.db'; " +
                        "if(Test-Path -LiteralPath $legacy){ Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue }; " +
                        "$dir=Join-Path $env:LOCALAPPDATA 'Microsoft\\Windows\\Explorer'; " +
                        "if(Test-Path -LiteralPath $dir){ Get-ChildItem -LiteralPath $dir -Filter 'iconcache*.db' -File -Force -ErrorAction SilentlyContinue | " +
                        "  Remove-Item -Force -ErrorAction SilentlyContinue }; " +
                        "if(@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count -eq 0){ Start-Process explorer }; " +
                        "Start-Sleep -Seconds 2; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ], ct),
            VerifyExplorerRunningAsync),
        new DelegateFixAction("shell.clear-thumbnails", "Fix_Shell_ClearThumbnails", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "shell-thumbnails", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                        "$dir=Join-Path $env:LOCALAPPDATA 'Microsoft\\Windows\\Explorer'; " +
                        "if(Test-Path -LiteralPath $dir){ Get-ChildItem -LiteralPath $dir -Filter 'thumbcache*.db' -File -Force -ErrorAction SilentlyContinue | " +
                        "  Remove-Item -Force -ErrorAction SilentlyContinue }; " +
                        "if(@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count -eq 0){ Start-Process explorer }; " +
                        "Start-Sleep -Seconds 2; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ], ct),
            VerifyExplorerRunningAsync),
        ExpandedRepairHelpers.RestartNamedServices(
            "shell.restart-shell-services", "Fix_Shell_RestartShellServices", Id,
            ["Themes", "ShellHWDetection", "FontCache"], "Themes"),
        new DelegateFixAction("shell.rebuild-font-cache", "Fix_Shell_RebuildFontCache", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureServicesAsync(["FontCache"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "foreach($n in @('FontCache','FontCache3.0.0.0')){ try{ Stop-Service -Name $n -Force -ErrorAction SilentlyContinue }catch{} }; " +
                        "Start-Sleep -Seconds 1; " +
                        "$dir=Join-Path $env:windir 'ServiceProfiles\\LocalService\\AppData\\Local\\FontCache'; " +
                        "if(Test-Path -LiteralPath $dir){ Get-ChildItem -LiteralPath $dir -Filter '*FontCache*' -File -Force -ErrorAction SilentlyContinue | " +
                        "  Remove-Item -Force -ErrorAction SilentlyContinue }; " +
                        "$legacy=Join-Path $env:windir 'ServiceProfiles\\LocalService\\AppData\\Local\\FontCache-System.dat'; " +
                        "if(Test-Path -LiteralPath $legacy){ Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue }; " +
                        "try{ Start-Service -Name FontCache -ErrorAction SilentlyContinue }catch{}; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "FontCache", ct)),
        new DelegateFixAction("shell.fix-user-folders", "Fix_Shell_FixUserFolders", Id, RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "shell-user-folders", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "$key='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders'; " +
                        "if(Test-Path $key){ $p=Get-Item -Path $key; foreach($n in $p.GetValueNames()){ " +
                        "  $raw=[string]$p.GetValue($n,$null,'DoNotExpandEnvironmentNames'); if([string]::IsNullOrWhiteSpace($raw)){ continue }; " +
                        "  $expanded=[Environment]::ExpandEnvironmentVariables($raw); " +
                        "  if($expanded -notmatch '^[A-Za-z]:\\\\'){ continue }; " +
                        "  if(-not (Test-Path -LiteralPath $expanded)){ " +
                        "    try{ New-Item -ItemType Directory -Path $expanded -Force -ErrorAction SilentlyContinue | Out-Null }catch{} } } }; exit 0"
                    ],
                    TimeSpan.FromMinutes(3))
            ], ct),
            async (context, ct) =>
            {
                const string script =
                    "$key='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders'; $missing=0; " +
                    "if(Test-Path $key){ $p=Get-Item -Path $key; foreach($n in $p.GetValueNames()){ " +
                    "  $raw=[string]$p.GetValue($n,$null,'DoNotExpandEnvironmentNames'); if([string]::IsNullOrWhiteSpace($raw)){ continue }; " +
                    "  $expanded=[Environment]::ExpandEnvironmentVariables($raw); " +
                    "  if($expanded -notmatch '^[A-Za-z]:\\\\'){ continue }; " +
                    "  if(-not (Test-Path -LiteralPath $expanded)){ $missing++ } } }; " +
                    "[pscustomobject]@{Missing=$missing}";
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row => Double(row, "Missing") == 0);
            }),
        new DelegateFixAction("shell.clear-jumplists", "Fix_Shell_ClearJumpLists", Id, RiskTier.Moderate,
            BackupJumpListsAsync,
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "$recent=Join-Path $env:APPDATA 'Microsoft\\Windows\\Recent'; " +
                        "foreach($sub in @('AutomaticDestinations','CustomDestinations')){ " +
                        "  $dir=Join-Path $recent $sub; " +
                        "  if(Test-Path -LiteralPath $dir){ Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue | " +
                        "    Remove-Item -Force -ErrorAction SilentlyContinue } }; exit 0"
                    ],
                    TimeSpan.FromMinutes(3))
            ], ct),
            async (_, _) => await Task.FromResult(true)),
        new DelegateFixAction("shell.reset-folder-views", "Fix_Shell_ResetFolderViews", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.CaptureRegistryOrMarkerAsync(
                context,
                @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell",
                "shell-folder-views",
                ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("reg.exe", ["delete", @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep("reg.exe", ["delete", @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU", "/f"]) { AcceptedExitCodes = new HashSet<int> { 0, 1 } },
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                        "if(@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count -eq 0){ Start-Process explorer }; " +
                        "Start-Sleep -Seconds 2; exit 0"
                    ],
                    TimeSpan.FromMinutes(3))
            ], ct),
            VerifyExplorerRunningAsync)
    ];

    private static async Task<bool> VerifyExplorerRunningAsync(FixContext context, CancellationToken ct)
    {
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            "[pscustomobject]@{Count=@(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count}",
            ct).ConfigureAwait(false);
        return ModuleHelpers.Array(root).Any(row => Double(row, "Count") > 0);
    }

    private static async Task<BackupEntry?> BackupJumpListsAsync(FixContext context, CancellationToken ct)
    {
        var dir = ModuleHelpers.BackupDirectory(context);
        var recent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Recent");
        var sources = new[]
        {
            Path.Combine(recent, "AutomaticDestinations"),
            Path.Combine(recent, "CustomDestinations")
        };
        if (!sources.Any(Directory.Exists))
        {
            return await ModuleHelpers.TransientMarkerAsync(context, "shell-jumplists-empty", ct).ConfigureAwait(false);
        }

        if (!TryMeasureDirectories(sources, 256L * 1024 * 1024, 5_000, out var bytes) ||
            !HasFreeSpace(dir, bytes + 32L * 1024 * 1024))
        {
            return null;
        }

        var quarantine = Path.Combine(dir, "JumpLists");
        foreach (var source in sources)
        {
            if (!Directory.Exists(source)) continue;
            var target = Path.Combine(quarantine, Path.GetFileName(source));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                }
                catch
                {
                    // Locked jump-list entries are skipped; the repair itself also skips locked files.
                }
            }
        }

        var escapedRecent = recent.Replace("'", "''", StringComparison.Ordinal);
        var restore =
            "$ErrorActionPreference='Continue'; $recent='" + escapedRecent + "'; " +
            "foreach($sub in @('AutomaticDestinations','CustomDestinations')){ " +
            "  $src=Join-Path '{backup}' $sub; $dst=Join-Path $recent $sub; " +
            "  if(Test-Path -LiteralPath $src){ New-Item -ItemType Directory -Path $dst -Force | Out-Null; " +
            "    Get-ChildItem -LiteralPath $src -File -Force -ErrorAction SilentlyContinue | " +
            "      Copy-Item -Destination $dst -Force -ErrorAction SilentlyContinue } }; exit 0";
        return await context.Backups.CaptureExistingCommandStateAsync(
            "Jump lists",
            quarantine,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", restore],
            ct).ConfigureAwait(false);
    }
}
