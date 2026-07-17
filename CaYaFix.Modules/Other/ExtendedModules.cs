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
        [new Playbook("camera.not-working", Id, "Symptom_Camera_NotWorking",
            ["camera.devices", "camera.privacy"],
            ["camera.allow-access", "camera.enable-disabled", "camera.scan-devices", "camera.restart-frameserver", "camera.restart-device", "camera.allow-desktop-apps"])])
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
            var finding = ModuleHelpers.Finding("camera.devices", Id, Severity.Warning, "Finding_Camera_DeviceError", Join(bad),
                "camera.enable-disabled", "camera.scan-devices", "camera.restart-device", "camera.restart-frameserver");
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
                ? ModuleHelpers.Finding("camera.privacy", Id, Severity.Warning, "Finding_Camera_PrivacyBlocked", row.ToString(),
                    "camera.allow-access", "camera.allow-desktop-apps")
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
        // Desktop apps global camera access (Microsoft privacy ConsentStore).
        new DelegateFixAction("camera.allow-desktop-apps", "Fix_Camera_AllowDesktopApps", Id, RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureRegistryAsync(
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam",
                ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("reg.exe", ["add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"]),
                new CommandStep("reg.exe", ["add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged", "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ], ct),
            async (context, ct) => (await context.Commands.RunAsync(
                "reg.exe",
                ["query", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "/v", "Value"],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false)).StdOut.Contains("Allow", StringComparison.OrdinalIgnoreCase)),
        ExpandedRepairHelpers.EnableDisabledPnp(
            "camera.enable-disabled", "Fix_Camera_EnableDisabled", Id,
            "$_.Class -in @('Camera','Image')", "Camera|Webcam|Integrated Camera"),
        ExpandedRepairHelpers.PnpScanDevices("camera.scan-devices", "Fix_Camera_ScanDevices", Id),
        // Frame Server hosts camera pipeline for UWP/desktop capture apps.
        ExpandedRepairHelpers.RestartNamedServices(
            "camera.restart-frameserver", "Fix_Camera_RestartFrameServer", Id,
            ["FrameServer", "FrameServerMonitor"], "FrameServer"),
        ExpandedRepairHelpers.RestartNamedServices(
            "camera.cycle-capture-services", "Fix_Camera_CycleCaptureServices", Id,
            ["FrameServer", "FrameServerMonitor", "DeviceInstall"], "FrameServer"),
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
        [new Playbook("usb.not-working", Id, "Symptom_Usb_NotWorking",
            ["usb.devices", "usb.services"],
            ["usb.start-services", "usb.scan-devices", "usb.enable-disabled", "usb.restart-hubs", "usb.cycle-root-hubs", "usb.restart-device"])])
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
                ? ModuleHelpers.Finding("usb.services", Id, Severity.Warning, "Finding_Usb_ServiceDisabled", Join(rows),
                    "usb.start-services", "usb.restart-discovery-services")
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
        ExpandedRepairHelpers.RestartNamedServices(
            "usb.restart-discovery-services", "Fix_Usb_RestartDiscoveryServices", Id,
            ["PlugPlay", "DsmSvc", "DeviceInstall"], "PlugPlay"),
        ExpandedRepairHelpers.PnpScanDevices("usb.scan-devices", "Fix_Usb_ScanDevices", Id),
        ExpandedRepairHelpers.EnableDisabledPnp(
            "usb.enable-disabled", "Fix_Usb_EnableDisabled", Id,
            "$_.InstanceId -like 'USB*'", "USB|Hub|Composite"),
        ExpandedRepairHelpers.RestartPnpByClass(
            "usb.restart-hubs", "Fix_Usb_RestartHubs", Id, "USB", maxDevices: 10),
        ExpandedRepairHelpers.TransientCommand(
            "usb.cycle-root-hubs",
            "Fix_Usb_CycleRootHubs",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
                        "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { " +
                        "  $_.FriendlyName -match 'Root Hub|USB Root|Generic USB Hub' -and $_.Status -eq 'OK' " +
                        "} | Select-Object -First 6 | ForEach-Object { " +
                        "  & $tool /restart-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 400 " +
                        "}; & $tool /scan-devices | Out-Null; exit 0"
                    ],
                    TimeSpan.FromMinutes(8))
            ],
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "pnputil.exe",
                    ["/enum-devices", "/class", "USB"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success),
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
        [
            new Playbook("search.not-working", Id, "Symptom_Search_NotWorking",
                ["search.service", "search.errors"],
                ["search.set-automatic", "search.restart-service", "search.rebuild-index", "search.clear-temp", "search.restart-and-rebuild"]),
            new Playbook("search.slow-index", Id, "Symptom_Search_SlowIndex",
                ["search.service", "search.errors"],
                ["search.rebuild-index", "search.clear-temp", "search.restart-service"])
        ])
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
                ? ModuleHelpers.Finding("search.service", Id, Severity.Warning, "Finding_Search_ServiceStopped", Join(rows),
                    "search.set-automatic", "search.restart-service", "search.restart-and-rebuild")
                : null;
        }),
        new DelegateDiagnosticCheck("search.errors", "Check_Search_Errors", Id, async (context, ct) =>
        {
            const string script = "$since=(Get-Date).AddDays(-3); Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='Microsoft-Windows-Search';StartTime=$since;Level=2} -MaxEvents 8 -ErrorAction SilentlyContinue | Select-Object TimeCreated,Id,Message";
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length > 0
                ? ModuleHelpers.Finding("search.errors", Id, Severity.Info, "Finding_Search_RecentErrors", Join(rows),
                    "search.restart-service", "search.rebuild-index", "search.clear-temp")
                : null;
        }, supportsPostRepairVerification: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction("search.restart-service", "Fix_Search_RestartService", Id, RiskTier.Safe,
            (context, ct) => context.Backups.CaptureServicesAsync(["WSearch"], ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service WSearch -StartupType Automatic; Restart-Service WSearch -Force -ErrorAction Stop"], TimeSpan.FromMinutes(3))], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "WSearch", ct)),
        // Microsoft: WSearch should be Automatic for desktop search.
        ExpandedRepairHelpers.TransientCommand(
            "search.set-automatic",
            "Fix_Search_SetAutomatic",
            Id,
            RiskTier.Safe,
            [
                new CommandStep("sc.exe", ["config", "WSearch", "start=", "auto"]),
                new CommandStep("sc.exe", ["start", "WSearch"])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1056 }
                }
            ],
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "WSearch", ct)),
        // Microsoft / FSLogix guidance: SetupCompletedSuccessfully=0 triggers catalog rebuild.
        new DelegateFixAction(
            "search.rebuild-index",
            "Fix_Search_RebuildIndex",
            Id,
            RiskTier.Moderate,
            (context, ct) => context.Backups.CaptureRegistryAsync(
                @"HKLM\SOFTWARE\Microsoft\Windows Search",
                ModuleHelpers.BackupDirectory(context),
                ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("reg.exe", ["add", @"HKLM\SOFTWARE\Microsoft\Windows Search", "/v", "SetupCompletedSuccessfully", "/t", "REG_DWORD", "/d", "0", "/f"]),
                new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "try{ Restart-Service WSearch -Force -ErrorAction SilentlyContinue }catch{}; exit 0"], TimeSpan.FromMinutes(3))
            ], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync(
                    "reg.exe",
                    ["query", @"HKLM\SOFTWARE\Microsoft\Windows Search", "/v", "SetupCompletedSuccessfully"],
                    TimeSpan.FromMinutes(1),
                    ct).ConfigureAwait(false);
                return result.Success;
            },
            requiresReboot: true),
        ExpandedRepairHelpers.TransientCommand(
            "search.clear-temp",
            "Fix_Search_ClearTemp",
            Id,
            RiskTier.Moderate,
            [
                new CommandStep(
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$ErrorActionPreference='Continue'; " +
                        "Stop-Service WSearch -Force -ErrorAction SilentlyContinue; " +
                        "$temp=Join-Path $env:ProgramData 'Microsoft\\Search\\Data\\Temp'; " +
                        "if(Test-Path -LiteralPath $temp){ Get-ChildItem -LiteralPath $temp -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; " +
                        "Set-Service WSearch -StartupType Automatic -ErrorAction SilentlyContinue; " +
                        "Start-Service WSearch -ErrorAction SilentlyContinue; exit 0"
                    ],
                    TimeSpan.FromMinutes(5))
            ],
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "WSearch", ct)),
        new DelegateFixAction(
            "search.restart-and-rebuild",
            "Fix_Search_RestartAndRebuild",
            Id,
            RiskTier.Moderate,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var services = await context.Backups.CaptureServicesAsync(["WSearch"], dir, ct).ConfigureAwait(false);
                var registry = await context.Backups.CaptureRegistryAsync(
                    @"HKLM\SOFTWARE\Microsoft\Windows Search", dir, ct).ConfigureAwait(false);
                return services is not null && registry is not null
                    ? await context.Backups.CaptureBundleAsync("search-rebuild", [services, registry], dir, ct)
                        .ConfigureAwait(false)
                    : services ?? registry;
            },
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("sc.exe", ["config", "WSearch", "start=", "auto"]),
                new CommandStep("reg.exe", ["add", @"HKLM\SOFTWARE\Microsoft\Windows Search", "/v", "SetupCompletedSuccessfully", "/t", "REG_DWORD", "/d", "0", "/f"]),
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", "try{ Restart-Service WSearch -Force -ErrorAction Stop }catch{ Start-Service WSearch -ErrorAction SilentlyContinue }; exit 0"],
                    TimeSpan.FromMinutes(3))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "WSearch", ct),
            requiresReboot: true)
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
                ["display.adapters", "display.errors", "display.tdr", "display.driver", "display.modes", "display.monitors"],
                ["display.soft-reset", "display.repair-resolution", "display.scan-devices", "display.restart-all", "display.restart-device"]),
            new Playbook(
                "display.resolution-stuck",
                Id,
                "Symptom_Display_ResolutionStuck",
                ["display.modes", "display.monitors", "display.adapters", "display.driver", "display.errors"],
                ["display.repair-resolution", "display.restore-recommended", "display.soft-reset", "display.scan-devices", "display.restart-all"])
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
                "display.repair-resolution",
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
                "display.repair-resolution",
                "display.scan-devices",
                "display.restart-all",
                "display.restart-device");
            var target = ModuleHelpers.GetString(bad[0], "DeviceID");
            if (!string.IsNullOrWhiteSpace(target)) finding.RepairParameters["display.restart-device.target"] = target;
            return finding;
        }),
        // Detect "hardware supports more modes but Windows won't let you change resolution"
        // (Basic Display Adapter, sparse EnumDisplaySettings list, sub-native mode, policy lock).
        new DelegateDiagnosticCheck("display.modes", "Check_Display_Modes", Id, CheckDisplayModesAsync),
        new DelegateDiagnosticCheck("display.monitors", "Check_Display_Monitors", Id, CheckDisplayMonitorsAsync),
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
                    "display.repair-resolution",
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

    private static async Task<Finding?> CheckDisplayModesAsync(DiagnosticContext context, CancellationToken ct)
    {
        // Microsoft documents unexpected/locked resolution as a common symptom of a broken or fallback
        // graphics driver (Basic Display Adapter). EnumDisplaySettings exposes the real mode list.
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<DisplayModeProbe.PathInfo> paths;
        try
        {
            paths = DisplayModeProbe.EnumerateActivePaths();
        }
        catch
        {
            paths = [];
        }

        const string adapterScript =
            "$adapters=@(Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Select-Object Status,FriendlyName,InstanceId,Problem); " +
            "$basic=@($adapters | Where-Object { [string]$_.FriendlyName -match 'Basic Display|Microsoft Basic' }); " +
            "$policy=$false; " +
            "foreach($rootKey in @('HKCU:\\Software\\Policies\\Microsoft\\Windows\\Control Panel\\Desktop','HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Control Panel\\Desktop','HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System','HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System')){ " +
            "  if(Test-Path $rootKey){ " +
            "    $p=Get-ItemProperty -Path $rootKey -ErrorAction SilentlyContinue; " +
            "    if($null -ne $p.NoDispSettingsPage -and [int]$p.NoDispSettingsPage -ne 0){ $policy=$true }; " +
            "    if($null -ne $p.NoDispAppearancePage -and [int]$p.NoDispAppearancePage -ne 0){ $policy=$true } " +
            "  } " +
            "}; " +
            "[pscustomobject]@{Adapters=@($adapters);BasicAdapterCount=$basic.Count;PolicyLocked=$policy}";

        JsonElement? adapterRow = null;
        try
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(adapterScript, ct).ConfigureAwait(false);
            adapterRow = ModuleHelpers.Array(root).FirstOrDefault();
        }
        catch
        {
            // Adapter/policy probe is best-effort; path enumeration still applies.
        }

        var basicCount = adapterRow is { ValueKind: JsonValueKind.Object } row ? GetInt(row, "BasicAdapterCount") : 0;
        var policyLocked = adapterRow is { ValueKind: JsonValueKind.Object } pol && IsTrue(pol, "PolicyLocked");
        var pathDetail = string.Join(
            Environment.NewLine,
            paths.Select(path =>
                $"{path.Device} ({path.Name}): current={path.CurrentWidth}x{path.CurrentHeight}; " +
                $"modes={path.ModeCount}; max={path.MaxWidth}x{path.MaxHeight}@{path.MaxFrequency}"));
        if (adapterRow is { ValueKind: JsonValueKind.Object } detailRow)
        {
            pathDetail = string.IsNullOrWhiteSpace(pathDetail)
                ? detailRow.ToString()
                : pathDetail + Environment.NewLine + detailRow;
        }

        if (basicCount > 0)
        {
            return ModuleHelpers.Finding(
                "display.modes",
                Id,
                Severity.Critical,
                "Finding_Display_BasicAdapter",
                pathDetail,
                "display.repair-resolution",
                "display.soft-reset",
                "display.scan-devices",
                "display.restart-all");
        }

        if (policyLocked)
        {
            return ModuleHelpers.Finding(
                "display.modes",
                Id,
                Severity.Warning,
                "Finding_Display_PolicyLocked",
                pathDetail);
        }

        var sparse = paths.Where(DisplayModeProbe.IsSparseOrSubNative).ToArray();
        if (sparse.Length > 0)
        {
            var belowNative = sparse.Any(DisplayModeProbe.IsSubNativeWithModes);
            var sparseDetail = string.Join(
                Environment.NewLine,
                sparse.Select(path =>
                    $"{path.Device}: current={path.CurrentWidth}x{path.CurrentHeight}; " +
                    $"modes={path.ModeCount}; max={path.MaxWidth}x{path.MaxHeight}"));
            return ModuleHelpers.Finding(
                "display.modes",
                Id,
                belowNative ? Severity.Warning : Severity.Critical,
                belowNative ? "Finding_Display_SubNativeResolution" : "Finding_Display_ModesLocked",
                sparseDetail,
                "display.repair-resolution",
                "display.restore-recommended",
                "display.soft-reset",
                "display.scan-devices",
                "display.restart-all");
        }

        return null;
    }

    private static async Task<Finding?> CheckDisplayMonitorsAsync(DiagnosticContext context, CancellationToken ct)
    {
        // Broken/disabled monitors and Generic PnP/Unknown devices often leave multi-monitor
        // resolution pickers greyed out even when the GPU supports more modes.
        const string script =
            "$mon=@(); " +
            "try{ $mon=@(Get-PnpDevice -Class Monitor -PresentOnly -ErrorAction SilentlyContinue | " +
            "  Select-Object Status,FriendlyName,InstanceId,Problem,Class) }catch{}; " +
            "$bad=@($mon | Where-Object { " +
            "  $_.Status -ne 'OK' -or [string]$_.Problem -match 'CM_PROB_DISABLED|22|DISABLED|ERROR|FAILED' -or [string]$_.Status -eq 'Error' " +
            "}); " +
            "$generic=@($mon | Where-Object { " +
            "  [string]$_.FriendlyName -match 'Generic PnP|Generic Non-PnP|Default Monitor|Unknown' " +
            "}); " +
            "[pscustomobject]@{MonitorCount=$mon.Count;Bad=@($bad);Generic=@($generic);All=@($mon)}";

        try
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var row = ModuleHelpers.Array(root).FirstOrDefault();
            if (row.ValueKind != JsonValueKind.Object) return null;

            var bad = GetArray(row, "Bad");
            if (bad.Length > 0)
            {
                var finding = ModuleHelpers.Finding(
                    "display.monitors",
                    Id,
                    Severity.Warning,
                    "Finding_Display_MonitorError",
                    Join(bad),
                    "display.repair-resolution",
                    "display.scan-devices",
                    "display.soft-reset");
                var target = ModuleHelpers.GetString(bad[0], "InstanceId");
                if (!string.IsNullOrWhiteSpace(target))
                {
                    finding.RepairParameters["display.restart-device.target"] = target;
                }

                return finding;
            }

            // Generic-only identification on multi-monitor is a weak signal — only raise when count >= 2.
            var monitorCount = GetInt(row, "MonitorCount");
            var generic = GetArray(row, "Generic");
            if (monitorCount >= 2 && generic.Length >= 1 && generic.Length == monitorCount)
            {
                return ModuleHelpers.Finding(
                    "display.monitors",
                    Id,
                    Severity.Info,
                    "Finding_Display_GenericMonitors",
                    Join(generic),
                    "display.repair-resolution",
                    "display.scan-devices");
            }

            if (monitorCount == 0)
            {
                return ModuleHelpers.Finding(
                    "display.monitors",
                    Id,
                    Severity.Warning,
                    "Finding_Display_NoMonitor",
                    row.ToString(),
                    "display.scan-devices",
                    "display.repair-resolution",
                    "display.soft-reset");
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

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
            VerifyDisplayAdaptersOkAsync),
        new DelegateFixAction(
            "display.repair-resolution",
            "Fix_Display_RepairResolution",
            Id,
            RiskTier.Moderate,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var adapters = await context.Backups.CaptureCommandStateAsync(
                    "display-adapters-res",
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "Get-PnpDevice -Class Display,Monitor -PresentOnly -ErrorAction SilentlyContinue | Select-Object InstanceId,Class,Status,Problem,FriendlyName | ConvertTo-Json -Compress"
                    ],
                    "powershell.exe",
                    [
                        "-NoProfile", "-NonInteractive", "-Command",
                        "$state=@(Get-Content -Raw -LiteralPath '{backup}'|ConvertFrom-Json); $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
                        "@($state)|ForEach-Object{ if($_.InstanceId -and ($_.Status -eq 'Error' -or [string]$_.Problem -match '22|DISABLED')){ & $tool /disable-device $_.InstanceId | Out-Null } }"
                    ],
                    dir,
                    ct).ConfigureAwait(false);
                var marker = await ModuleHelpers.TransientMarkerAsync(context, "display-repair-resolution", ct)
                    .ConfigureAwait(false);
                return adapters is not null && marker is not null
                    ? await context.Backups.CaptureBundleAsync("display-resolution-repair", [adapters, marker], dir, ct)
                        .ConfigureAwait(false)
                    : adapters ?? marker;
            },
            ApplyRepairResolutionAsync,
            async (context, ct) =>
            {
                // After repair: prefer no Basic adapter and at least one OK display device.
                const string script =
                    "$adapters=@(Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue); " +
                    "$basic=@($adapters | Where-Object { $_.FriendlyName -match 'Basic Display|Microsoft Basic' }); " +
                    "$ok=@($adapters | Where-Object Status -eq 'OK'); " +
                    "[pscustomobject]@{Basic=$basic.Count;Ok=$ok.Count;Total=$adapters.Count}";
                try
                {
                    var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
                    var row = ModuleHelpers.Array(root).FirstOrDefault();
                    if (row.ValueKind != JsonValueKind.Object) return await VerifyDisplayAdaptersOkAsync(context, ct).ConfigureAwait(false);
                    return GetInt(row, "Basic") == 0 && GetInt(row, "Ok") > 0;
                }
                catch
                {
                    return await VerifyDisplayAdaptersOkAsync(context, ct).ConfigureAwait(false);
                }
            }),
        new DelegateFixAction(
            "display.restore-recommended",
            "Fix_Display_RestoreRecommended",
            Id,
            RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "display-restore-recommended", ct),
            (context, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var log = DisplayModeProbe.ApplyHighestSupportedModes();
                    return Task.FromResult(FixResult.Ok("FixResult_Applied", log));
                }
                catch (Exception ex)
                {
                    return Task.FromResult(FixResult.Fail("FixResult_ApplyFailed", ex.Message));
                }
            },
            (context, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var paths = DisplayModeProbe.EnumerateActivePaths();
                    return Task.FromResult(DisplayModeProbe.PathsLookHealthy(paths));
                }
                catch
                {
                    return Task.FromResult(true);
                }
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
            VerifyDisplayAdaptersOkAsync),
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

    /// <summary>
    /// Microsoft-oriented repair for "resolution greyed out / can't change despite hardware support":
    /// re-enable display+monitor devices, restart adapters, soft-reset GPU, rescan PnP.
    /// </summary>
    private static async Task<FixResult> ApplyRepairResolutionAsync(FixContext context, CancellationToken ct)
    {
        var details = new List<string>();
        var enableScript =
            "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
            "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { " +
            "  $_.Class -in @('Display','Monitor') -and " +
            "  ($_.Status -eq 'Error' -or $_.Problem -match 'CM_PROB_DISABLED|22|DISABLED' -or $_.Status -eq 'Unknown') " +
            "} | ForEach-Object { & $tool /enable-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 250 }; exit 0";
        var enable = await context.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", enableScript],
            TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);
        details.Add($"enable-display-monitor: exit={enable.ExitCode}");

        var restartScript =
            "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
            "Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object { " +
            "  & $tool /restart-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 500 " +
            "}; " +
            "Get-PnpDevice -Class Monitor -PresentOnly -ErrorAction SilentlyContinue | Where-Object Status -eq 'OK' | " +
            "  Select-Object -First 4 | ForEach-Object { & $tool /restart-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 300 }; " +
            "exit 0";
        var restart = await context.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", restartScript],
            TimeSpan.FromMinutes(8),
            ct).ConfigureAwait(false);
        details.Add($"restart-adapters-monitors: exit={restart.ExitCode}");

        try
        {
            await Task.Run(() => GraphicsDriverHotkey.TriggerWinCtrlShiftB(), ct).ConfigureAwait(false);
            details.Add("soft-reset: Win+Ctrl+Shift+B triggered");
        }
        catch (Exception ex)
        {
            details.Add("soft-reset: " + ex.Message);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        var scan = await context.Commands.RunAsync(
            "pnputil.exe",
            ["/scan-devices"],
            TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);
        details.Add($"scan-devices: exit={scan.ExitCode}");

        return FixResult.Ok("FixResult_Applied", string.Join(Environment.NewLine, details));
    }

    private static async Task<bool> VerifyDisplayAdaptersOkAsync(FixContext context, CancellationToken ct)
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
    }

    private static string Join(IEnumerable<JsonElement> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => row.ToString()));

    private static int GetInt(JsonElement element, string property) =>
        int.TryParse(ModuleHelpers.GetString(element, property), out var value) ? value : 0;

    private static bool IsTrue(JsonElement element, string property)
    {
        var text = ModuleHelpers.GetString(element, property);
        return string.Equals(text, "True", StringComparison.OrdinalIgnoreCase) ||
               text == "1";
    }

    private static JsonElement[] GetArray(JsonElement element, string property)
    {
        if (!ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToArray(),
            JsonValueKind.Object => [value],
            _ => []
        };
    }
}
