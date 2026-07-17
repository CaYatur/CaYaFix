// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;

namespace CaYaFix.Modules.Other;

public sealed class CameraPrivacyModule : WindowsModuleBase
{
    private const string Id = "camera";

    public CameraPrivacyModule() : base(
        new ModuleInfo(Id, "Module_Camera_Name", "Module_Camera_Description", "camera.svg", 10),
        CreateChecks(), CreateFixes(),
        [new Playbook("camera.not-working", Id, "Symptom_Camera_NotWorking", ["camera.devices", "camera.privacy"], ["camera.restart-device", "camera.allow-access"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("camera.devices", "Check_Camera_Devices", Id, async (context, ct) =>
        {
            const string script = "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {$_.Class -in @('Camera','Image')} | Select-Object Status,Class,FriendlyName,InstanceId";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root).Where(row =>
                !string.Equals(ModuleHelpers.GetString(row, "Status"), "OK", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bad.Length == 0) return null;
            var finding = ModuleHelpers.Finding("camera.devices", Id, Severity.Warning, "Finding_Camera_DeviceError", Join(bad), "camera.restart-device");
            var target = ModuleHelpers.GetString(bad[0], "InstanceId");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["camera.restart-device.target"] = target;
            return finding;
        }),
        new DelegateDiagnosticCheck("camera.privacy", "Check_Camera_Privacy", Id, async (context, ct) =>
        {
            const string script = "$devices=@(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {$_.Class -in @('Camera','Image')}); $value=(Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\webcam' -Name Value -ErrorAction SilentlyContinue).Value; [pscustomobject]@{DeviceCount=$devices.Count;Value=$value}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            return row.ValueKind == JsonValueKind.Object &&
                   double.TryParse(ModuleHelpers.GetString(row, "DeviceCount"), out var count) && count > 0 &&
                   string.Equals(ModuleHelpers.GetString(row, "Value"), "Deny", StringComparison.OrdinalIgnoreCase)
                ? ModuleHelpers.Finding("camera.privacy", Id, Severity.Warning, "Finding_Camera_PrivacyBlocked", row.ToString(), "camera.allow-access")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("camera.allow-access", "Fix_Camera_AllowAccess", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureRegistryAsync(@"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("reg.exe", ["add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"])], ct),
            async (context, ct) => (await context.Commands.RunAsync("reg.exe", ["query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "/v", "Value"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).StdOut.Contains("Allow", StringComparison.OrdinalIgnoreCase)),
        RestartDeviceFix("camera.restart-device", "Fix_Camera_RestartDevice", Id, "camera.restart-device.target")
    ];

    private static DelegateFixAction RestartDeviceFix(string fixId, string titleKey, string moduleId, string targetKey) => new(
        fixId, titleKey, moduleId, RiskTier.Moderate,
        (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "camera-device", targetKey, ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/restart-device", context.Parameters[targetKey]], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/instanceid", context.Parameters[targetKey]], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
        requiresTarget: true);

    private static string Join(IEnumerable<JsonElement> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
}

public sealed class UsbDevicesModule : WindowsModuleBase
{
    private const string Id = "usb";

    public UsbDevicesModule() : base(
        new ModuleInfo(Id, "Module_Usb_Name", "Module_Usb_Description", "usb.svg", 11),
        CreateChecks(), CreateFixes(),
        [new Playbook("usb.not-working", Id, "Symptom_Usb_NotWorking", ["usb.devices", "usb.services"], ["usb.restart-device", "usb.start-services"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("usb.devices", "Check_Usb_Devices", Id, async (context, ct) =>
        {
            const string script = "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {$_.InstanceId -like 'USB*' -and $_.Status -ne 'OK'} | Select-Object Status,Class,FriendlyName,InstanceId,Problem";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            if (rows.Length == 0) return null;
            var finding = ModuleHelpers.Finding("usb.devices", Id, Severity.Warning, "Finding_Usb_DeviceError", Join(rows), "usb.restart-device");
            var target = ModuleHelpers.GetString(rows[0], "InstanceId");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["usb.restart-device.target"] = target;
            return finding;
        }),
        new DelegateDiagnosticCheck("usb.services", "Check_Usb_Services", Id, async (context, ct) =>
        {
            const string script = "Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('PlugPlay','DsmSvc','DeviceInstall')} | Select-Object Name,State,StartMode";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var plugAndPlay = rows.FirstOrDefault(row =>
                string.Equals(ModuleHelpers.GetString(row, "Name"), "PlugPlay", StringComparison.OrdinalIgnoreCase));
            var unhealthy = plugAndPlay.ValueKind != JsonValueKind.Object ||
                            !string.Equals(ModuleHelpers.GetString(plugAndPlay, "State"), "Running", StringComparison.OrdinalIgnoreCase) ||
                            rows.Any(row => string.Equals(
                                ModuleHelpers.GetString(row, "StartMode"),
                                "Disabled",
                                StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("usb.services", Id, Severity.Warning, "Finding_Usb_ServiceDisabled", Join(rows), "usb.start-services")
                : null;
        })
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("usb.start-services", "Fix_Usb_StartServices", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["PlugPlay", "DsmSvc", "DeviceInstall"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service DsmSvc -StartupType Manual; Set-Service DeviceInstall -StartupType Manual; Start-Service PlugPlay,DsmSvc,DeviceInstall -ErrorAction SilentlyContinue"])], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "PlugPlay", ct)),
        new DelegateFixAction("usb.restart-device", "Fix_Usb_RestartDevice", Id, RiskTier.Moderate,
            (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "usb-device", "usb.restart-device.target", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("pnputil.exe", ["/restart-device", context.Parameters["usb.restart-device.target"]], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/instanceid", context.Parameters["usb.restart-device.target"]], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
            requiresTarget: true)
    ];

    private static string Join(IEnumerable<JsonElement> rows) => string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
}

public sealed class WindowsSearchModule : WindowsModuleBase
{
    private const string Id = "search";

    public WindowsSearchModule() : base(
        new ModuleInfo(Id, "Module_Search_Name", "Module_Search_Description", "search-index.svg", 12),
        CreateChecks(), CreateFixes(),
        [new Playbook("search.not-working", Id, "Symptom_Search_NotWorking", ["search.service", "search.errors"], ["search.restart-service"])])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("search.service", "Check_Search_Service", Id, async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-CimInstance Win32_Service -Filter \"Name='WSearch'\" | Select-Object Name,State,StartMode", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            var unhealthy = rows.Length != 1 || rows.Any(row =>
                string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase));
            return unhealthy
                ? ModuleHelpers.Finding("search.service", Id, Severity.Warning, "Finding_Search_ServiceStopped", Join(rows), "search.restart-service")
                : null;
        }),
        new DelegateDiagnosticCheck("search.errors", "Check_Search_Errors", Id, async (context, ct) =>
        {
            const string script = "$since=(Get-Date).AddDays(-3); Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='Microsoft-Windows-Search';StartTime=$since;Level=2} -MaxEvents 8 -ErrorAction SilentlyContinue | Select-Object TimeCreated,Id,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding("search.errors", Id, Severity.Info, "Finding_Search_RecentErrors", Join(rows), "search.restart-service")
                : null;
        }, supportsPostRepairVerification: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("search.restart-service", "Fix_Search_RestartService", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["WSearch"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service WSearch -StartupType Automatic; Restart-Service WSearch -Force -ErrorAction Stop"], TimeSpan.FromMinutes(3))], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "WSearch", ct))
    ];

    private static string Join(IEnumerable<JsonElement> rows) => string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
}

public sealed class DisplayGraphicsModule : WindowsModuleBase
{
    private const string Id = "display";

    public DisplayGraphicsModule() : base(
        new ModuleInfo(Id, "Module_Display_Name", "Module_Display_Description", "display.svg", 13),
        CreateChecks(), CreateFixes(),
        [
            new Playbook(
                "display.unstable",
                Id,
                "Symptom_Display_Unstable",
                ["display.adapters", "display.errors", "display.tdr", "display.driver"],
                ["display.soft-reset", "display.scan-devices", "display.restart-all", "display.restart-device"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("display.adapters", "Check_Display_Adapters", Id, async (context, ct) =>
        {
            const string script = "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object {[pscustomobject]@{Status=$_.Status;Name=$_.FriendlyName;InstanceId=$_.InstanceId;Problem=$_.Problem}}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root).Where(row =>
                !string.Equals(ModuleHelpers.GetString(row, "Status"), "OK", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bad.Length == 0) return null;
            var finding = ModuleHelpers.Finding(
                "display.adapters",
                Id,
                Severity.Critical,
                "Finding_Display_AdapterError",
                Join(bad),
                "display.soft-reset",
                "display.scan-devices",
                "display.restart-all",
                "display.restart-device");
            var target = ModuleHelpers.GetString(bad[0], "InstanceId");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["display.restart-device.target"] = target;
            return finding;
        }),
        new DelegateDiagnosticCheck("display.driver", "Check_Display_Driver", Id, async (context, ct) =>
        {
            const string script =
                "Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.DeviceClass -eq 'DISPLAY' -or $_.DeviceClass -eq 'Display' } | " +
                "ForEach-Object { " +
                "  $entity = Get-CimInstance Win32_PnPEntity -Filter (\"PNPDeviceID='\" + ($_.DeviceID -replace \"'\",\"''\") + \"'\") -ErrorAction SilentlyContinue; " +
                "  [pscustomobject]@{DeviceName=$_.DeviceName;DeviceID=$_.DeviceID;InfName=$_.InfName;DriverVersion=$_.DriverVersion;" +
                "    ConfigManagerErrorCode=if($entity){[int]$entity.ConfigManagerErrorCode}else{0}} " +
                "}";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var bad = ModuleHelpers.Array(root)
                .Where(row =>
                {
                    var codeText = ModuleHelpers.GetString(row, "ConfigManagerErrorCode");
                    return int.TryParse(codeText, out var code) && code != 0;
                })
                .ToArray();
            if (bad.Length == 0) return null;
            var finding = ModuleHelpers.Finding(
                "display.driver",
                Id,
                Severity.Critical,
                "Finding_Display_DriverError",
                Join(bad),
                "display.soft-reset",
                "display.scan-devices",
                "display.restart-all",
                "display.restart-device");
            var target = ModuleHelpers.GetString(bad[0], "DeviceID");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["display.restart-device.target"] = target;
            return finding;
        }),
        new DelegateDiagnosticCheck("display.errors", "Check_Display_Errors", Id, async (context, ct) =>
        {
            const string script =
                "$since=(Get-Date).AddDays(-3); " +
                "Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Display';StartTime=$since} -MaxEvents 12 -ErrorAction SilentlyContinue | " +
                "Select-Object TimeCreated,Id,ProviderName,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding(
                    "display.errors",
                    Id,
                    Severity.Warning,
                    "Finding_Display_RecentResets",
                    Join(rows),
                    "display.soft-reset",
                    "display.scan-devices",
                    "display.restart-all")
                : null;
        }, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("display.tdr", "Check_Display_Tdr", Id, async (context, ct) =>
        {
            // Correlate GPU Timeout Detection and Recovery (TDR) / dxgkrnl / display resets from System log.
            const string script =
                "$since=(Get-Date).AddDays(-7); " +
                "$providers=@('Display','Microsoft-Windows-DxgKrnl','nvlddmkm','amdkmdag','igfx','Intel-GFX-Driver'); " +
                "$events=@(); " +
                "foreach($p in $providers){ " +
                "  $events += @(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName=$p;StartTime=$since;Level=1,2,3} -MaxEvents 20 -ErrorAction SilentlyContinue) " +
                "}; " +
                "$events | Where-Object { " +
                "  $m=[string]$_.Message; $id=$_.Id; " +
                "  $id -in 4101,153,4105,0 -or $m -match 'Timeout Detection|TDR|Display driver|recovered from a timeout|GPU' " +
                "} | Select-Object -First 12 TimeCreated,Id,ProviderName,LevelDisplayName,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding(
                    "display.tdr",
                    Id,
                    Severity.Warning,
                    "Finding_Display_TdrEvents",
                    Join(rows),
                    "display.soft-reset",
                    "display.scan-devices",
                    "display.restart-all")
                : null;
        }, quick: false, supportsPostRepairVerification: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction(
            "display.soft-reset",
            "Fix_Display_SoftReset",
            Id,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "display-soft-reset", ct),
            async (context, ct) =>
            {
                // Same effect family as Win+Ctrl+Shift+B (graphics driver reset; brief black screen).
                ct.ThrowIfCancellationRequested();
                try
                {
                    await Task.Run(() => GraphicsDriverHotkey.TriggerWinCtrlShiftB(), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return FixResult.Fail("FixResult_CommandFailed", ex.Message);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var scan = await context.Commands.RunAsync(
                    "pnputil.exe",
                    ["/scan-devices"],
                    TimeSpan.FromMinutes(5),
                    ct).ConfigureAwait(false);
                return scan.Success
                    ? FixResult.Ok("FixResult_Applied", "Win+Ctrl+Shift+B graphics reset triggered; device scan completed.")
                    : FixResult.Ok(
                        "FixResult_Applied",
                        "Win+Ctrl+Shift+B graphics reset triggered; device scan reported: " + scan.StdErr);
            },
            async (context, ct) =>
            {
                const string script =
                    "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Select-Object Status";
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                var rows = ModuleHelpers.Array(root).ToArray();
                return rows.Length == 0 ||
                       rows.Any(row => string.Equals(
                           ModuleHelpers.GetString(row, "Status"),
                           "OK",
                           StringComparison.OrdinalIgnoreCase));
            }),
        new DelegateFixAction(
            "display.scan-devices",
            "Fix_Display_ScanDevices",
            Id,
            RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "display-scan-devices", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "pnputil.exe",
                    ["/enum-devices", "/class", "Display"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success),
        new DelegateFixAction(
            "display.restart-all",
            "Fix_Display_RestartAll",
            Id,
            RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureCommandStateAsync(
                "display-adapters",
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Select-Object InstanceId,Status,Problem | ConvertTo-Json -Compress"
                ],
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "$state=@(Get-Content -Raw -LiteralPath '{backup}'|ConvertFrom-Json); $tool=Join-Path $env:windir 'System32\\pnputil.exe'; @($state)|ForEach-Object{ if($_.InstanceId){ & $tool /restart-device $_.InstanceId | Out-Null } }"
                ],
                ModuleHelpers.BackupDirectory(context),
                ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
                        "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { " +
                        "  & $tool /restart-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 400 " +
                        "}; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ], ct),
            async (context, ct) =>
            {
                const string script =
                    "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | " +
                    "Select-Object Status";
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                var rows = ModuleHelpers.Array(root).ToArray();
                return rows.Length > 0 &&
                       rows.Any(row => string.Equals(
                           ModuleHelpers.GetString(row, "Status"),
                           "OK",
                           StringComparison.OrdinalIgnoreCase));
            }),
        new DelegateFixAction(
            "display.restart-device",
            "Fix_Display_RestartDevice",
            Id,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "display-device", "display.restart-device.target", ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("pnputil.exe", ["/restart-device", context.Parameters["display.restart-device.target"]], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) => (await context.Commands.RunAsync(
                "pnputil.exe",
                ["/enum-devices", "/instanceid", context.Parameters["display.restart-device.target"]],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false)).Success,
            requiresTarget: true)
    ];

    private static string Join(IEnumerable<JsonElement> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
}
