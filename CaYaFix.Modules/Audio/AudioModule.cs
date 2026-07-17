// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CaYaFix.Core;
using CaYaFix.Modules.Shared;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaYaFix.Modules.Audio;

public sealed class AudioModule : IModuleDefinition
{
    public const string ModuleId = "audio";
    private static readonly string[] AudioServices = ["AudioEndpointBuilder", "Audiosrv"];
    private static readonly string[] VirtualDeviceTokens =
        ["VB-Audio", "VB-Cable", "Voicemeeter", "Virtual", "NVIDIA Broadcast", "Steam Streaming", "Discord"];

    public AudioModule()
    {
        Checks = CreateChecks();
        Fixes = CreateFixes();
        LiveTests = CreateLiveTests();
        Playbooks = CreatePlaybooks();
    }

    public ModuleInfo Info { get; } = new(
        ModuleId,
        "Module_Audio_Name",
        "Module_Audio_Description",
        "audio.svg",
        1);

    public IReadOnlyList<DiagnosticCheck> Checks { get; }
    public IReadOnlyList<FixAction> Fixes { get; }
    public IReadOnlyList<LiveTest> LiveTests { get; }
    public IReadOnlyList<Playbook> Playbooks { get; }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("audio.output.devices", "Check_Audio_OutputDevices", ModuleId, CheckOutputDevicesAsync),
        new DelegateDiagnosticCheck("audio.input.devices", "Check_Audio_InputDevices", ModuleId, CheckInputDevicesAsync),
        new DelegateDiagnosticCheck("audio.defaults", "Check_Audio_DefaultDevices", ModuleId, CheckDefaultsAsync),
        new DelegateDiagnosticCheck("audio.services", "Check_Audio_Services", ModuleId, CheckServicesAsync),
        new DelegateDiagnosticCheck("audio.levels", "Check_Audio_Levels", ModuleId, CheckLevelsAsync),
        new DelegateDiagnosticCheck("audio.drivers", "Check_Audio_Drivers", ModuleId, CheckDriversAsync),
        new DelegateDiagnosticCheck("audio.formats", "Check_Audio_Formats", ModuleId, CheckFormatsAsync),
        new DelegateDiagnosticCheck("audio.apo", "Check_Audio_Enhancements", ModuleId, CheckEnhancementsAsync, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("audio.privacy", "Check_Audio_MicrophonePrivacy", ModuleId, CheckMicrophonePrivacyAsync),
        new DelegateDiagnosticCheck("audio.bluetooth", "Check_Audio_Bluetooth", ModuleId, CheckBluetoothAsync, quick: false),
        new DelegateDiagnosticCheck("audio.hdmi", "Check_Audio_Hdmi", ModuleId, CheckHdmiAsync, quick: false),
        new DelegateDiagnosticCheck("audio.ducking", "Check_Audio_Communications", ModuleId, CheckCommunicationsAsync),
        new DelegateDiagnosticCheck("audio.eventlog", "Check_Audio_EventLog", ModuleId, CheckEventLogAsync, quick: false, supportsPostRepairVerification: false),
        new DelegateDiagnosticCheck("audio.pnp-disabled", "Check_Audio_PnpDisabled", ModuleId, CheckPnpDisabledAsync)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        CreateRestartServicesFix(),
        CreateSetDefaultFix(),
        CreateUnmuteFix(),
        CreateRepairAllIoFix(),
        CreateRepairOutputFix(),
        CreateRepairInputFix(),
        CreateDisableEnhancementsFix(),
        CreateFormatResetFix(),
        CreateMicrophonePrivacyFix(),
        CreateMixerResetFix(),
        CreateBluetoothRestartFix(),
        CreateEnableDeviceFix(),
        CreateEnableAllDisabledFix(),
        CreateRescanDevicesFix(),
        CreateDriverResetFix(),
        CreateMmDevicesResetFix()
    ];

    private static IReadOnlyList<LiveTest> CreateLiveTests() =>
    [
        new DelegateLiveTest("audio.live.speaker", "LiveTest_Audio_Speaker", ModuleId, RunSpeakerTestAsync),
        new DelegateLiveTest("audio.live.microphone", "LiveTest_Audio_Microphone", ModuleId, RunMicrophoneTestAsync),
        new DelegateLiveTest("audio.live.stability", "LiveTest_Audio_Stability", ModuleId, RunStabilityTestAsync)
    ];

    private static IReadOnlyList<Playbook> CreatePlaybooks() =>
    [
        new("audio.no-sound", ModuleId, "Symptom_Audio_NoSound",
            ["audio.output.devices", "audio.defaults", "audio.services", "audio.levels", "audio.drivers", "audio.pnp-disabled", "audio.eventlog"],
            ["audio.repair-output", "audio.restart-services", "audio.enable-all-disabled", "audio.rescan-devices", "audio.unmute", "audio.set-default", "audio.disable-enhancements"]),
        new("audio.crackle", ModuleId, "Symptom_Audio_Crackling",
            ["audio.formats", "audio.apo", "audio.drivers", "audio.bluetooth", "audio.eventlog"],
            ["audio.disable-enhancements", "audio.format-reset", "audio.restart-services", "audio.rescan-devices"]),
        new("audio.mic-none", ModuleId, "Symptom_Audio_MicNotWorking",
            ["audio.input.devices", "audio.privacy", "audio.levels", "audio.services", "audio.drivers", "audio.pnp-disabled"],
            ["audio.repair-input", "audio.microphone-privacy", "audio.enable-all-disabled", "audio.unmute", "audio.restart-services"]),
        new("audio.mic-low", ModuleId, "Symptom_Audio_MicLow",
            ["audio.levels", "audio.formats", "audio.apo"],
            ["audio.repair-input", "audio.unmute", "audio.disable-enhancements"]),
        new("audio.bluetooth-bad", ModuleId, "Symptom_Audio_BluetoothBad",
            ["audio.bluetooth", "audio.defaults", "audio.formats"],
            ["audio.set-default", "audio.bluetooth-restart", "audio.restart-services"]),
        new("audio.wrong-device", ModuleId, "Symptom_Audio_WrongDevice",
            ["audio.defaults", "audio.hdmi"],
            ["audio.set-default", "audio.restart-services"])
    ];

    private static Task<Finding?> CheckOutputDevicesAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var all = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToArray();
        try
        {
            var active = all.Where(device => device.State == DeviceState.Active).ToArray();
            var disabled = all.Where(device => device.State == DeviceState.Disabled).ToArray();
            if (active.Length == 0)
            {
                var finding = ModuleHelpers.Finding(
                    "audio.output.devices", ModuleId, Severity.Critical,
                    "Finding_Audio_NoOutputDevice", DescribeDevices(all),
                    "audio.restart-services", "audio.enable-device", "audio.mmdevices-reset");
                if (disabled.Length > 0)
                {
                    finding.RepairParameters["audio.enable-device.target"] = disabled[0].ID;
                }

                return Task.FromResult<Finding?>(finding);
            }

            if (disabled.Length > 0)
            {
                var finding = ModuleHelpers.Finding(
                    "audio.output.devices", ModuleId, Severity.Warning,
                    "Finding_Audio_NoOutputDevice", DescribeDevices(disabled),
                    "audio.enable-device");
                finding.RepairParameters["audio.enable-device.target"] = disabled[0].ID;
                return Task.FromResult<Finding?>(finding);
            }

            return Task.FromResult<Finding?>(null);
        }
        finally
        {
            DisposeDevices(all);
        }
    }

    private static Task<Finding?> CheckInputDevicesAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var all = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All).ToArray();
        try
        {
            var active = all.Where(device => device.State == DeviceState.Active).ToArray();
            if (active.Length == 0)
            {
                return Task.FromResult<Finding?>(ModuleHelpers.Finding(
                    "audio.input.devices", ModuleId, Severity.Critical,
                    "Finding_Audio_NoInputDevice", DescribeDevices(all), "audio.restart-services", "audio.mmdevices-reset"));
            }

            return Task.FromResult<Finding?>(null);
        }
        finally
        {
            DisposeDevices(all);
        }
    }

    private static Task<Finding?> CheckDefaultsAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        MMDevice current;
        try
        {
            current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return Task.FromResult<Finding?>(ModuleHelpers.Finding(
                "audio.defaults", ModuleId, Severity.Critical,
                "Finding_Audio_NoDefaultOutput", string.Empty, "audio.restart-services", "audio.mmdevices-reset"));
        }

        using (current)
        {
            var virtualToken = VirtualDeviceTokens.FirstOrDefault(token =>
                current.FriendlyName.Contains(token, StringComparison.OrdinalIgnoreCase));
            var isBrokenVirtual = virtualToken is not null && !IsVirtualCompanionRunning(virtualToken);
            var isUnplugged = current.State != DeviceState.Active;
            if (!isBrokenVirtual && !isUnplugged)
            {
                return Task.FromResult<Finding?>(null);
            }

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
            try
            {
                var physical = devices.FirstOrDefault(device => !VirtualDeviceTokens.Any(token =>
                    device.FriendlyName.Contains(token, StringComparison.OrdinalIgnoreCase)));
                string[] fixes = physical is null
                    ? new[] { "audio.restart-services", "audio.mmdevices-reset" }
                    : new[] { "audio.set-default", "audio.mmdevices-reset" };
                var finding = ModuleHelpers.Finding(
                    "audio.defaults", ModuleId, Severity.Critical,
                    isBrokenVirtual ? "Finding_Audio_VirtualDefaultInactive" : "Finding_Audio_DefaultUnavailable",
                    $"Default={current.FriendlyName}; State={current.State}", fixes);
                if (physical is not null)
                {
                    finding.RepairParameters["audio.set-default.target"] = physical.ID;
                }

                return Task.FromResult<Finding?>(finding);
            }
            finally
            {
                DisposeDevices(devices);
            }
        }
    }

    private static async Task<Finding?> CheckServicesAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "Get-CimInstance Win32_Service | Where-Object {$_.Name -in @('Audiosrv','AudioEndpointBuilder')} | Select-Object Name,State,StartMode";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        var bad = rows.Where(row =>
            !string.Equals(ModuleHelpers.GetString(row, "State"), "Running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ModuleHelpers.GetString(row, "StartMode"), "Disabled", StringComparison.OrdinalIgnoreCase)).ToArray();
        return rows.Length != AudioServices.Length || bad.Length > 0
            ? ModuleHelpers.Finding("audio.services", ModuleId, Severity.Critical,
                "Finding_Audio_ServiceStopped", string.Join(Environment.NewLine, rows.Select(row => row.ToString())), "audio.restart-services")
            : null;
    }

    private static Task<Finding?> CheckLevelsAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var issues = new List<string>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                if (device.AudioEndpointVolume.Mute || device.AudioEndpointVolume.MasterVolumeLevelScalar <= .005f)
                {
                    issues.Add($"{flow}: {device.FriendlyName}; mute={device.AudioEndpointVolume.Mute}; level={device.AudioEndpointVolume.MasterVolumeLevelScalar:P0}");
                }

            }
            catch
            {
                // Missing defaults are reported by the default-device check.
            }
        }

        return Task.FromResult<Finding?>(issues.Count > 0
            ? ModuleHelpers.Finding("audio.levels", ModuleId, Severity.Critical,
                "Finding_Audio_MutedOrZero", string.Join(Environment.NewLine, issues), "audio.unmute", "audio.mixer-reset")
            : null);
    }

    private static async Task<Finding?> CheckDriversAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$codes=@{}; Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue|ForEach-Object {$codes[$_.PNPDeviceID]=[int]$_.ConfigManagerErrorCode}; Get-CimInstance Win32_PnPSignedDriver | Where-Object {$_.DeviceClass -in @('MEDIA','AudioEndpoint')} | ForEach-Object {[pscustomobject]@{DeviceName=$_.DeviceName;DeviceID=$_.DeviceID;InfName=$_.InfName;DriverProviderName=$_.DriverProviderName;DriverDate=if($_.DriverDate){$_.DriverDate.ToString('o')}else{$null};ConfigManagerErrorCode=[int]$codes[$_.DeviceID]}}";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var bad = ModuleHelpers.Array(root).Where(row => GetInt(row, "ConfigManagerErrorCode") != 0).ToArray();
        if (bad.Length == 0)
        {
            return null;
        }

        var finding = ModuleHelpers.Finding("audio.drivers", ModuleId, Severity.Critical,
            "Finding_Audio_DriverError", string.Join(Environment.NewLine, bad.Select(row => row.ToString())), "audio.driver-reset");
        var inf = ModuleHelpers.GetString(bad[0], "InfName");
        if (!string.IsNullOrWhiteSpace(inf))
        {
            finding.RepairParameters["audio.driver-reset.target"] = inf;
        }

        return finding;
    }

    private static Task<Finding?> CheckFormatsAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var formats = new List<(string Name, int Rate, int Bits)>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                var format = device.AudioClient.MixFormat;
                formats.Add(($"{flow}: {device.FriendlyName}", format.SampleRate, format.BitsPerSample));
            }
            catch
            {
                // Reported by the device checks.
            }
        }

        var invalid = formats.Where(format =>
            format.Rate is < 8000 or > 384000 || format.Bits is < 8 or > 64).ToArray();
        var sampleRates = formats.Select(format => format.Rate).ToHashSet();
        var clockMismatch = sampleRates.Contains(44_100) && sampleRates.Contains(48_000);
        return Task.FromResult<Finding?>(invalid.Length > 0 || clockMismatch
            ? ModuleHelpers.Finding("audio.formats", ModuleId, Severity.Warning,
                "Finding_Audio_FormatMismatch", string.Join(Environment.NewLine, formats.Select(format => $"{format.Name}: {format.Rate} Hz / {format.Bits}-bit")), "audio.format-reset")
            : null);
    }

    private static async Task<Finding?> CheckEnhancementsAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script = "$since=(Get-Date).AddDays(-2); $events=@(Get-WinEvent -FilterHashtable @{LogName='System';StartTime=$since} -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -match 'Audio' -and $_.Message -match 'APO|enhancement|effect'} | Select-Object -First 8 TimeCreated,Id,ProviderName,Message); $events";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var events = ModuleHelpers.Array(root).ToArray();
        return events.Length > 0
            ? ModuleHelpers.Finding("audio.apo", ModuleId, Severity.Warning,
                "Finding_Audio_EnhancementCrash", string.Join(Environment.NewLine, events.Select(row => row.ToString())), "audio.disable-enhancements")
            : null;
    }

    private static Task<Finding?> CheckMicrophonePrivacyAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string path = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        var value = Registry.GetValue(path, "Value", null)?.ToString();
        return Task.FromResult<Finding?>(value?.Equals("Deny", StringComparison.OrdinalIgnoreCase) == true
            ? ModuleHelpers.Finding("audio.privacy", ModuleId, Severity.Critical,
                "Finding_Audio_MicrophonePrivacyDenied", $"Value={value}", "audio.microphone-privacy")
            : null);
    }

    private static async Task<Finding?> CheckBluetoothAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string currentName;
        string stereoName;
        string stereoId;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var handsFree = current.FriendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                            current.FriendlyName.Contains("AG Audio", StringComparison.OrdinalIgnoreCase) ||
                            current.FriendlyName.Contains("Eller Serbest", StringComparison.OrdinalIgnoreCase);
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
            try
            {
                var stereoAlternative = devices.FirstOrDefault(device =>
                    device.FriendlyName.Contains("Stereo", StringComparison.OrdinalIgnoreCase) &&
                    !device.FriendlyName.Equals(current.FriendlyName, StringComparison.OrdinalIgnoreCase));
                if (!handsFree || stereoAlternative is null)
                {
                    return null;
                }

                currentName = current.FriendlyName;
                stereoName = stereoAlternative.FriendlyName;
                stereoId = stereoAlternative.ID;
            }
            finally
            {
                DisposeDevices(devices);
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The default-device check reports a missing or inaccessible endpoint.
            return null;
        }

        const string script = "Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {$_.Class -in @('Bluetooth','AudioEndpoint','MEDIA')} | Select-Object FriendlyName,InstanceId,Class,Status";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var tokens = DeviceSpecificTokens(currentName)
            .Concat(DeviceSpecificTokens(stereoName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var target = ModuleHelpers.Array(root)
            .Select(row => new
            {
                Id = ModuleHelpers.GetString(row, "InstanceId"),
                Score = DeviceMatchScore(ModuleHelpers.GetString(row, "FriendlyName"), tokens)
            })
            .Where(candidate => candidate.Score > 0 && !string.IsNullOrWhiteSpace(candidate.Id) && candidate.Id!.Length <= 4_096)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        string[] fixes = target is null
            ? new[] { "audio.set-default" }
            : new[] { "audio.set-default", "audio.bluetooth-restart" };
        var finding = ModuleHelpers.Finding(
            "audio.bluetooth",
            ModuleId,
            Severity.Warning,
            "Finding_Audio_BluetoothHandsFree",
            $"Default={currentName}; Stereo={stereoName}",
            fixes);
        finding.RepairParameters["audio.set-default.target"] = stereoId;
        if (target is not null)
        {
            finding.RepairParameters["audio.bluetooth-restart.target"] = target.Id!;
        }
        return finding;
    }

    private static Task<Finding?> CheckHdmiAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var hdmi = current.FriendlyName.Contains("HDMI", StringComparison.OrdinalIgnoreCase) ||
                   current.FriendlyName.Contains("Display Audio", StringComparison.OrdinalIgnoreCase) ||
                   current.FriendlyName.Contains("monitor", StringComparison.OrdinalIgnoreCase);
        if (!hdmi)
        {
            return Task.FromResult<Finding?>(null);
        }

        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        try
        {
            var alternative = devices.FirstOrDefault(device =>
                !device.ID.Equals(current.ID, StringComparison.OrdinalIgnoreCase) &&
                !device.FriendlyName.Contains("HDMI", StringComparison.OrdinalIgnoreCase) &&
                !device.FriendlyName.Contains("Display Audio", StringComparison.OrdinalIgnoreCase));
            if (alternative is null)
            {
                return Task.FromResult<Finding?>(null);
            }

            var finding = ModuleHelpers.Finding("audio.hdmi", ModuleId, Severity.Warning,
                "Finding_Audio_HdmiDefault", $"Default={current.FriendlyName}; Alternative={alternative.FriendlyName}", "audio.set-default");
            finding.RepairParameters["audio.set-default.target"] = alternative.ID;
            return Task.FromResult<Finding?>(finding);
        }
        finally
        {
            DisposeDevices(devices);
        }
    }

    private static Task<Finding?> CheckCommunicationsAsync(DiagnosticContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string path = @"HKEY_CURRENT_USER\Software\Microsoft\Multimedia\Audio";
        var raw = Registry.GetValue(path, "UserDuckingPreference", 3);
        var value = Convert.ToInt32(raw);
        return Task.FromResult<Finding?>(value is < 0 or > 3
            ? ModuleHelpers.Finding("audio.ducking", ModuleId, Severity.Warning,
                "Finding_Audio_DuckingInvalid", $"UserDuckingPreference={value}", "audio.mixer-reset")
            : null);
    }

    private static async Task<Finding?> CheckEventLogAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script =
            "$since=(Get-Date).AddDays(-3); " +
            "$providers=@('Microsoft-Windows-Audio','Microsoft-Windows-Audio-EndpointBuilder','Audiosrv','AudioEndpointBuilder'); " +
            "$events=@(); " +
            "foreach($p in $providers){ " +
            "  $events += @(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName=$p;StartTime=$since;Level=1,2,3} -MaxEvents 10 -ErrorAction SilentlyContinue); " +
            "  $events += @(Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName=$p;StartTime=$since;Level=1,2,3} -MaxEvents 10 -ErrorAction SilentlyContinue) " +
            "}; " +
            "$events | Sort-Object TimeCreated -Descending | Select-Object -First 12 TimeCreated,Id,ProviderName,LevelDisplayName,Message";
        try
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length >= 2
                ? ModuleHelpers.Finding(
                    "audio.eventlog",
                    ModuleId,
                    Severity.Warning,
                    "Finding_Audio_EventLogErrors",
                    string.Join(Environment.NewLine, rows.Select(row => row.ToString())),
                    "audio.restart-services",
                    "audio.rescan-devices",
                    "audio.enable-all-disabled")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Finding?> CheckPnpDisabledAsync(DiagnosticContext context, CancellationToken ct)
    {
        const string script =
            "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
            "Where-Object { " +
            "  ($_.Class -in @('MEDIA','AudioEndpoint','SoftwareDevice') -or $_.FriendlyName -match 'Audio|Sound|Speaker|Microphone|Realtek|NVIDIA High Definition|AMD High Definition') -and " +
            "  ($_.Status -eq 'Error' -or $_.Problem -match 'CM_PROB_DISABLED|22|DISABLED' -or $_.Status -eq 'Unknown') " +
            "} | Select-Object Status,Class,FriendlyName,InstanceId,Problem";
        var root = await context.Commands.RunPsJsonAsync<JsonElement>(script, ct).ConfigureAwait(false);
        var rows = ModuleHelpers.Array(root).ToArray();
        if (rows.Length == 0) return null;
        var finding = ModuleHelpers.Finding(
            "audio.pnp-disabled",
            ModuleId,
            Severity.Warning,
            "Finding_Audio_PnpDisabled",
            string.Join(Environment.NewLine, rows.Select(row => row.ToString())),
            "audio.enable-all-disabled",
            "audio.enable-device",
            "audio.rescan-devices",
            "audio.restart-services");
        var target = ModuleHelpers.GetString(rows[0], "InstanceId");
        if (!string.IsNullOrWhiteSpace(target))
        {
            finding.RepairParameters["audio.enable-device.target"] = target;
        }

        return finding;
    }

    private static DelegateFixAction CreateRestartServicesFix() => new(
        "audio.restart-services", "Fix_Audio_RestartServices", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureServicesAsync(AudioServices, ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Set-Service AudioEndpointBuilder -StartupType Automatic; Set-Service Audiosrv -StartupType Automatic; Restart-Service AudioEndpointBuilder -Force -ErrorAction Stop; Restart-Service Audiosrv -Force -ErrorAction Stop"], TimeSpan.FromMinutes(3))], ct),
        async (context, ct) =>
        {
            var root = await context.Commands.RunPsJsonAsync<JsonElement>("Get-Service AudioEndpointBuilder,Audiosrv | Select-Object Name,Status", ct).ConfigureAwait(false);
            var rows = ModuleHelpers.Array(root).ToArray();
            return rows.Length == 2 && rows.All(row =>
                string.Equals(ModuleHelpers.GetString(row, "Status"), "Running", StringComparison.OrdinalIgnoreCase));
        });

    private static DelegateFixAction CreateRepairAllIoFix() =>
        CreateEndpointRepairCore("audio.repair-all-io", "Fix_Audio_RepairAllIo", AudioEndpointScope.All);

    private static DelegateFixAction CreateRepairOutputFix() =>
        CreateEndpointRepairCore("audio.repair-output", "Fix_Audio_RepairOutput", AudioEndpointScope.Output);

    private static DelegateFixAction CreateRepairInputFix() =>
        CreateEndpointRepairCore("audio.repair-input", "Fix_Audio_RepairInput", AudioEndpointScope.Input);

    private static DelegateFixAction CreateEndpointRepairCore(string id, string titleKey, AudioEndpointScope scope) => new(
        id,
        titleKey,
        ModuleId,
        RiskTier.Safe,
        async (context, ct) =>
        {
            var directory = ModuleHelpers.BackupDirectory(context);
            var services = await context.Backups.CaptureServicesAsync(AudioServices, directory, ct).ConfigureAwait(false);
            var levels = CaptureLevels(scope);
            var levelsEntry = levels.Count > 0
                ? await context.Backups.CaptureValueAsync("audio-levels-" + scope, levels, directory, ct).ConfigureAwait(false)
                : null;
            if (levelsEntry is not null)
            {
                levelsEntry.Metadata["restoreHandler"] = "audio-levels-v1";
            }

            var entries = new List<BackupEntry>();
            if (services is not null) entries.Add(services);
            if (levelsEntry is not null) entries.Add(levelsEntry);
            if (entries.Count == 0) return null;
            return entries.Count == 1
                ? entries[0]
                : await context.Backups.CaptureBundleAsync("audio-endpoint-" + scope, entries, directory, ct)
                    .ConfigureAwait(false);
        },
        (context, ct) => ApplyEndpointRepairAsync(context, scope, ct),
        async (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in FlowsFor(scope))
            {
                try
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                    if (device.AudioEndpointVolume.Mute) return false;
                }
                catch
                {
                    // Default endpoint may be missing; services still count as partial success.
                }
            }

            return await ModuleHelpers.IsServiceRunningAsync(context, "Audiosrv", ct).ConfigureAwait(false);
        });

    private static async Task<FixResult> ApplyEndpointRepairAsync(
        FixContext context,
        AudioEndpointScope scope,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var details = new List<string>();

        // 1) Unmute and restore levels on scoped endpoints (defaults + active devices).
        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var flow in FlowsFor(scope))
            {
                var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToArray();
                try
                {
                    foreach (var device in devices)
                    {
                        try
                        {
                            device.AudioEndpointVolume.Mute = false;
                            if (device.AudioEndpointVolume.MasterVolumeLevelScalar < .5f)
                            {
                                device.AudioEndpointVolume.MasterVolumeLevelScalar = .65f;
                            }
                        }
                        catch
                        {
                            // Individual endpoint may be exclusive/locked.
                        }
                    }

                    details.Add($"{flow}: unmuted {devices.Length} active endpoint(s)");
                }
                finally
                {
                    DisposeDevices(devices);
                }
            }
        }

        // 2) Microphone privacy (input scopes only).
        if (scope is AudioEndpointScope.Input or AudioEndpointScope.All)
        {
            var privacy = await context.Commands.RunAsync(
                "reg.exe",
                [
                    "add",
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone",
                    "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"
                ],
                TimeSpan.FromMinutes(1),
                ct).ConfigureAwait(false);
            details.Add($"microphone-privacy: exit={privacy.ExitCode}");
        }

        // 3) Re-enable disabled PnP audio devices matching the selected flow.
        var nameFilter = scope switch
        {
            AudioEndpointScope.Output => "Speaker|Headphone|Headset|Realtek|NVIDIA High Definition|AMD High Definition|HDMI|Display Audio|Output|Line Out",
            AudioEndpointScope.Input => "Microphone|Mic |Input|Capture|Array|Webcam",
            _ => "Audio|Sound|Speaker|Microphone|Realtek|NVIDIA High Definition|AMD High Definition|Headphone|Headset|HDMI"
        };
        var classFilter = scope switch
        {
            AudioEndpointScope.Output => "@('MEDIA','AudioEndpoint')",
            AudioEndpointScope.Input => "@('MEDIA','AudioEndpoint','Camera','Image')",
            _ => "@('MEDIA','AudioEndpoint','SoftwareDevice')"
        };
        var enableScript =
            "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
            "$namePattern='" + nameFilter + "'; " +
            "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { " +
            "  ($_.Class -in " + classFilter + " -or $_.FriendlyName -match $namePattern) -and " +
            "  ($_.Status -eq 'Error' -or $_.Problem -match 'CM_PROB_DISABLED|22|DISABLED' -or $_.Status -eq 'Unknown') " +
            "} | ForEach-Object { & $tool /enable-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 200 }; exit 0";
        var enable = await context.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", enableScript],
            TimeSpan.FromMinutes(5),
            ct).ConfigureAwait(false);
        details.Add($"enable-disabled: exit={enable.ExitCode}");

        // 4) Soft format cleanup on scoped MMDevices branch (Render and/or Capture only).
        var branches = scope switch
        {
            AudioEndpointScope.Output => new[] { "Render" },
            AudioEndpointScope.Input => new[] { "Capture" },
            _ => new[] { "Render", "Capture" }
        };
        var branchList = string.Join(",", branches.Select(b => $"'{b}'"));
        var formatScript =
            "$ErrorActionPreference='Continue'; " +
            "$root='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio'; " +
            "foreach($b in @(" + branchList + ")){ " +
            "  $path=Join-Path $root $b; if(-not (Test-Path -LiteralPath $path)){ continue }; " +
            "  Get-ChildItem $path -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -eq 'Properties' } | " +
            "    ForEach-Object { Remove-ItemProperty -Path $_.PSPath -Name '{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0' -ErrorAction SilentlyContinue } " +
            "}; exit 0";
        var format = await context.Commands.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", formatScript],
            TimeSpan.FromMinutes(3),
            ct).ConfigureAwait(false);
        details.Add($"format-soft-reset: exit={format.ExitCode}");

        // 5) Restart Windows audio stack last so endpoint changes take effect.
        var services = await context.Commands.RunAsync(
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                "Set-Service AudioEndpointBuilder -StartupType Automatic -ErrorAction SilentlyContinue; " +
                "Set-Service Audiosrv -StartupType Automatic -ErrorAction SilentlyContinue; " +
                "Restart-Service AudioEndpointBuilder -Force -ErrorAction SilentlyContinue; " +
                "Start-Sleep -Milliseconds 500; " +
                "Restart-Service Audiosrv -Force -ErrorAction SilentlyContinue; exit 0"
            ],
            TimeSpan.FromMinutes(3),
            ct).ConfigureAwait(false);
        details.Add($"audio-services: exit={services.ExitCode}");

        return FixResult.Ok("FixResult_Applied", string.Join(Environment.NewLine, details));
    }

    private static DataFlow[] FlowsFor(AudioEndpointScope scope) => scope switch
    {
        AudioEndpointScope.Output => [DataFlow.Render],
        AudioEndpointScope.Input => [DataFlow.Capture],
        _ => [DataFlow.Render, DataFlow.Capture]
    };

    private enum AudioEndpointScope
    {
        All,
        Output,
        Input
    }

    private static DelegateFixAction CreateSetDefaultFix() => new(
        "audio.set-default", "Fix_Audio_SetDefault", ModuleId, RiskTier.Safe,
        async (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            var state = new Dictionary<string, string>();
            foreach (var role in Enum.GetValues<Role>())
            {
                foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                {
                    try
                    {
                        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
                        state[$"{flow}.{role}"] = device.ID;
                    }
                    catch { }
                }
            }
            if (state.Count == 0) return null;
            var entry = await context.Backups.CaptureValueAsync("audio-defaults", state, ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
            if (entry is not null) entry.Metadata["restoreHandler"] = "audio-defaults-v1";
            return entry;
        },
        (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var success = AudioPolicyConfig.SetDefaultEndpoint(context.Parameters["audio.set-default.target"]);
            return Task.FromResult(success ? FixResult.Ok("FixResult_Applied") : FixResult.Fail("FixResult_ApplyFailed"));
        },
        (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Task.FromResult(current.ID.Equals(context.Parameters["audio.set-default.target"], StringComparison.OrdinalIgnoreCase));
        },
        requiresTarget: true);

    private static DelegateFixAction CreateUnmuteFix() => new(
        "audio.unmute", "Fix_Audio_Unmute", ModuleId, RiskTier.Safe,
        async (context, ct) =>
        {
            var levels = CaptureLevels();
            var entry = await context.Backups.CaptureValueAsync("audio-levels", levels, ModuleHelpers.BackupDirectory(context), ct).ConfigureAwait(false);
            if (entry is not null) entry.Metadata["restoreHandler"] = "audio-levels-v1";
            return entry;
        },
        (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                try
                {
                    using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                    device.AudioEndpointVolume.Mute = false;
                    if (device.AudioEndpointVolume.MasterVolumeLevelScalar < .5f)
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = .65f;
                    }
                }
                catch { }
            }
            return Task.FromResult(FixResult.Ok("FixResult_Applied"));
        },
        (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return Task.FromResult(!render.AudioEndpointVolume.Mute && render.AudioEndpointVolume.MasterVolumeLevelScalar > .01f);
            }
            catch { return Task.FromResult(false); }
        });

    private static DelegateFixAction CreateDisableEnhancementsFix() => new(
        "audio.disable-enhancements", "Fix_Audio_DisableEnhancements", ModuleId, RiskTier.Moderate,
        (context, ct) => CaptureAudioRegistryAndServicesAsync(context, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio", "audio-enhancements", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "$root='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio'; Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.PSChildName -eq 'FxProperties'} | ForEach-Object {New-ItemProperty -Path $_.PSPath -Name '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5' -PropertyType DWord -Value 1 -Force | Out-Null}; Restart-Service Audiosrv -Force"], TimeSpan.FromMinutes(3))], ct),
        async (context, ct) =>
        {
            var result = await context.Commands.RunAsync("powershell.exe", ["-NoProfile", "-Command", "$root='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio'; $fx=@(Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.PSChildName -eq 'FxProperties'}); $bad=@($fx|Where-Object {(Get-ItemPropertyValue $_.PSPath -Name '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5' -ErrorAction SilentlyContinue) -ne 1}); if($bad.Count -eq 0){exit 0}else{exit 1}"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            return result.Success;
        });

    private static DelegateFixAction CreateFormatResetFix() => new(
        "audio.format-reset", "Fix_Audio_FormatReset", ModuleId, RiskTier.Moderate,
        (context, ct) => CaptureAudioRegistryAndServicesAsync(context, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio", "audio-format", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-Command", "$root='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio'; Stop-Service Audiosrv -Force; Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.PSChildName -eq 'Properties'} | ForEach-Object {Remove-ItemProperty -Path $_.PSPath -Name '{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0' -ErrorAction SilentlyContinue}; Start-Service Audiosrv"], TimeSpan.FromMinutes(3))], ct),
        async (context, ct) => await CheckFormatsAsync(new DiagnosticContext
        {
            Commands = context.Commands,
            Text = context.Text,
            Thresholds = new Thresholds(),
            SessionDirectory = context.Session.DirectoryPath
        }, ct).ConfigureAwait(false) is null);

    private static DelegateFixAction CreateMicrophonePrivacyFix() => new(
        "audio.microphone-privacy", "Fix_Audio_MicrophonePrivacy", ModuleId, RiskTier.Moderate,
        (context, ct) => context.Backups.CaptureRegistryAsync(@"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", ModuleHelpers.BackupDirectory(context), ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("reg.exe", ["add", @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "/v", "Value", "/t", "REG_SZ", "/d", "Allow", "/f"])], ct),
        async (context, ct) => await CheckMicrophonePrivacyAsync(new DiagnosticContext { Commands = context.Commands, Text = context.Text, Thresholds = new Thresholds(), SessionDirectory = context.Session.DirectoryPath }, ct).ConfigureAwait(false) is null);

    private static DelegateFixAction CreateMixerResetFix() => new(
        "audio.mixer-reset", "Fix_Audio_MixerReset", ModuleId, RiskTier.Moderate,
        (context, ct) => CaptureAudioRegistryAndServicesAsync(context, @"HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore", "audio-mixer", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("reg.exe", ["delete", @"HKCU\Software\Microsoft\Internet Explorer\LowRegistry\Audio\PolicyConfig\PropertyStore", "/f"]), new CommandStep("powershell.exe", ["-NoProfile", "-Command", "Restart-Service Audiosrv -Force"])], ct),
        (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, "Audiosrv", ct));

    private static DelegateFixAction CreateBluetoothRestartFix() => new(
        "audio.bluetooth-restart", "Fix_Audio_BluetoothRestart", ModuleId, RiskTier.Moderate,
        (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "audio-bluetooth-device", "audio.bluetooth-restart.target", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/restart-device", context.Parameters["audio.bluetooth-restart.target"]], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/enum-devices", "/instanceid", context.Parameters["audio.bluetooth-restart.target"]], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false)).Success,
        requiresTarget: true);

    private static DelegateFixAction CreateEnableDeviceFix() => new(
        "audio.enable-device", "Fix_Audio_EnableDevice", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.CapturePnpDeviceStateAsync(context, "audio-enable-device", "audio.enable-device.target", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/enable-device", context.Parameters["audio.enable-device.target"]], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) =>
        {
            if (!context.Parameters.TryGetValue("audio.enable-device.target", out var target) ||
                string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var result = await context.Commands.RunAsync(
                "pnputil.exe",
                ["/enum-devices", "/instanceid", target],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            return result.Success &&
                   !result.StdOut.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        },
        requiresTarget: true);

    private static DelegateFixAction CreateEnableAllDisabledFix() => new(
        "audio.enable-all-disabled", "Fix_Audio_EnableAllDisabled", ModuleId, RiskTier.Safe,
        (context, ct) => context.Backups.CaptureCommandStateAsync(
            "audio-disabled-devices",
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.Class -in @('MEDIA','AudioEndpoint') -or $_.FriendlyName -match 'Audio|Sound|Speaker|Microphone' } | " +
                "Select-Object InstanceId,Status,Problem | ConvertTo-Json -Compress"
            ],
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                "$state=@(Get-Content -Raw -LiteralPath '{backup}'|ConvertFrom-Json); $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
                "@($state)|ForEach-Object{ if($_.InstanceId -and ($_.Status -eq 'Error' -or [string]$_.Problem -match '22|DISABLED')){ & $tool /disable-device $_.InstanceId | Out-Null } }"
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
                    "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { " +
                    "  ($_.Class -in @('MEDIA','AudioEndpoint') -or $_.FriendlyName -match 'Audio|Sound|Speaker|Microphone|Realtek') -and " +
                    "  ($_.Status -eq 'Error' -or $_.Problem -match 'CM_PROB_DISABLED|22|DISABLED' -or $_.Status -eq 'Unknown') " +
                    "} | ForEach-Object { & $tool /enable-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 250 }; " +
                    "try { Restart-Service AudioEndpointBuilder -Force -ErrorAction SilentlyContinue; Restart-Service Audiosrv -Force -ErrorAction SilentlyContinue } catch {}; exit 0"
                ],
                TimeSpan.FromMinutes(6))
        ], ct),
        async (context, ct) =>
            (await context.Commands.RunAsync("sc.exe", ["query", "Audiosrv"], TimeSpan.FromMinutes(1), ct)
                .ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateRescanDevicesFix() => new(
        "audio.rescan-devices", "Fix_Audio_RescanDevices", ModuleId, RiskTier.Safe,
        (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "audio-rescan-devices", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
        [
            new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5)),
            new CommandStep(
                "powershell.exe",
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "try{ Restart-Service AudioEndpointBuilder -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 400; Restart-Service Audiosrv -Force -ErrorAction SilentlyContinue }catch{}; exit 0"
                ],
                TimeSpan.FromMinutes(3))
        ], ct),
        async (context, ct) =>
            (await context.Commands.RunAsync("sc.exe", ["query", "Audiosrv"], TimeSpan.FromMinutes(1), ct)
                .ConfigureAwait(false)).Success);

    private static DelegateFixAction CreateDriverResetFix() => new(
        "audio.driver-reset", "Fix_Audio_DriverReset", ModuleId, RiskTier.Aggressive,
        async (context, ct) =>
        {
            var directory = ModuleHelpers.BackupDirectory(context);
            var audioState = await CaptureAudioRegistryAndServicesAsync(
                context, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio", "audio-driver-state", ct).ConfigureAwait(false);
            var driver = await context.Backups.CaptureDriverAsync(
                context.Parameters["audio.driver-reset.target"], directory, ct).ConfigureAwait(false);
            return audioState is not null && driver is not null
                ? await context.Backups.CaptureBundleAsync("audio-driver", [audioState, driver], directory, ct).ConfigureAwait(false)
                : null;
        },
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("pnputil.exe", ["/delete-driver", context.Parameters["audio.driver-reset.target"], "/uninstall", "/force"], TimeSpan.FromMinutes(10)), new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false)).Success,
        requiresReboot: true,
        requiresTarget: true);

    private static DelegateFixAction CreateMmDevicesResetFix() => new(
        "audio.mmdevices-reset", "Fix_Audio_MmDevicesReset", ModuleId, RiskTier.Aggressive,
        (context, ct) => CaptureAudioRegistryAndServicesAsync(context, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices", "audio-mmdevices", ct),
        (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [new CommandStep("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$root='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio'; Stop-Service Audiosrv -Force; Stop-Service AudioEndpointBuilder -Force; Remove-Item \"$root\\Render\" -Recurse -Force -ErrorAction Stop; Remove-Item \"$root\\Capture\" -Recurse -Force -ErrorAction Stop; Start-Service AudioEndpointBuilder; Start-Service Audiosrv"], TimeSpan.FromMinutes(8)), new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
        async (context, ct) => (await context.Commands.RunAsync("sc.exe", ["query", "Audiosrv"], TimeSpan.FromMinutes(1), ct).ConfigureAwait(false)).Success,
        requiresReboot: true);

    private static async IAsyncEnumerable<TestProgress> RunSpeakerTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var provider = new StereoToneProvider();
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        output.Init(provider);
        output.Play();

        provider.ActiveChannel = 0;
        yield return new TestProgress("audio.live.speaker", "TestStage_Left", .25, device.FriendlyName);
        await Task.Delay(900, ct).ConfigureAwait(false);
        provider.ActiveChannel = -1;
        await Task.Delay(200, ct).ConfigureAwait(false);
        provider.ActiveChannel = 1;
        yield return new TestProgress("audio.live.speaker", "TestStage_Right", .65, device.FriendlyName);
        await Task.Delay(900, ct).ConfigureAwait(false);
        provider.ActiveChannel = -1;
        output.Stop();
        yield return new TestProgress("audio.live.speaker", "TestStage_HeardQuestion", 1, device.FriendlyName, RequiresUserAnswer: true);
    }

    private static async IAsyncEnumerable<TestProgress> RunMicrophoneTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        const int maximumCaptureBytes = 8 * 1024 * 1024;
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        using var capture = new WasapiCapture(device);
        using var sample = new MemoryStream(capacity: 1024 * 1024);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peakGate = new object();
        double peak = 0;
        var acceptingSamples = true;
        Exception? recordingError = null;

        capture.DataAvailable += (_, args) =>
        {
            lock (peakGate)
            {
                if (!acceptingSamples) return;
                var measured = CalculatePeak(args.Buffer, args.BytesRecorded, capture.WaveFormat);
                peak = Math.Max(peak, measured);
                var remaining = maximumCaptureBytes - (int)sample.Length;
                if (remaining > 0)
                {
                    sample.Write(args.Buffer, 0, Math.Min(args.BytesRecorded, remaining));
                }
            }
        };
        capture.RecordingStopped += (_, args) =>
        {
            recordingError = args.Exception;
            stopped.TrySetResult(true);
        };
        var recordingStarted = false;
        var finalPeak = 0d;
        byte[] captured = [];
        try
        {
            capture.StartRecording();
            recordingStarted = true;
            for (var index = 0; index < 20; index++)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                double current;
                lock (peakGate) current = peak;
                yield return new TestProgress(
                    "audio.live.microphone",
                    "TestStage_Recording",
                    (index + 1) / 20d * .68,
                    device.FriendlyName,
                    new Dictionary<string, double> { ["Peak"] = current });
            }

            capture.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            recordingStarted = false;
            lock (peakGate)
            {
                acceptingSamples = false;
                finalPeak = peak;
                captured = sample.ToArray();
            }

            if (recordingError is not null)
            {
                yield return new TestProgress(
                    "audio.live.microphone",
                    "TestStage_Failed",
                    1,
                    context.Text.Get("TestResult_Failed"),
                    new Dictionary<string, double> { ["Peak"] = finalPeak });
                yield break;
            }

            if (finalPeak > .005 && captured.Length > 0)
            {
                yield return new TestProgress(
                    "audio.live.microphone",
                    "TestStage_Playback",
                    .78,
                    device.FriendlyName,
                    new Dictionary<string, double> { ["Peak"] = finalPeak });
                var playbackSucceeded = await PlayCapturedAudioAsync(captured, capture.WaveFormat, ct).ConfigureAwait(false);
                if (!playbackSucceeded)
                {
                    yield return new TestProgress(
                        "audio.live.microphone",
                        "TestStage_PlaybackUnavailable",
                        .9,
                        device.FriendlyName,
                        new Dictionary<string, double> { ["Peak"] = finalPeak });
                }
            }

            yield return new TestProgress(
                "audio.live.microphone",
                finalPeak <= .005 ? "TestStage_NoSignal" : "TestStage_Recorded",
                1,
                device.FriendlyName,
                new Dictionary<string, double> { ["Peak"] = finalPeak },
                RequiresUserAnswer: true);
        }
        finally
        {
            lock (peakGate)
            {
                acceptingSamples = false;
            }
            if (recordingStarted)
            {
                try { capture.StopRecording(); } catch { }
            }
            lock (peakGate)
            {
                Array.Clear(captured, 0, captured.Length);
                if (sample.TryGetBuffer(out var buffer) && buffer.Array is not null)
                {
                    Array.Clear(buffer.Array, buffer.Offset, (int)sample.Length);
                }
            }
        }
    }

    private static async Task<bool> PlayCapturedAudioAsync(
        byte[] captured,
        WaveFormat format,
        CancellationToken ct)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var outputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var stream = new MemoryStream(captured, writable: false);
            using var provider = new RawSourceWaveStream(stream, format);
            using var output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 80);
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? playbackError = null;
            output.PlaybackStopped += (_, args) =>
            {
                playbackError = args.Exception;
                stopped.TrySetResult(true);
            };
            output.Init(provider);
            output.Play();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            return playbackError is null;
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

    private static async IAsyncEnumerable<TestProgress> RunStabilityTestAsync(
        DiagnosticContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var provider = new StereoToneProvider { ActiveChannel = 2, Gain = .03f };
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        Exception? playbackError = null;
        output.PlaybackStopped += (_, args) => playbackError = args.Exception;
        output.Init(provider);
        output.Play();
        for (var index = 0; index < 10; index++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            yield return new TestProgress("audio.live.stability", "TestStage_Running", (index + 1) / 10d, $"{index + 1}/10 s");
        }
        output.Stop();
        yield return new TestProgress("audio.live.stability", playbackError is null ? "TestStage_Result" : "TestStage_Failed", 1,
            playbackError?.Message ?? device.FriendlyName,
            new Dictionary<string, double> { ["Underruns"] = playbackError is null ? 0 : 1 });
    }

    public static async Task<bool> RestoreDefaultDevicesBackupAsync(BackupEntry backup, CancellationToken ct)
    {
        if (!File.Exists(backup.Location)) return false;
        var state = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(backup.Location, ct).ConfigureAwait(false)) ?? [];
        if (state.Count is 0 or > 6 || state.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 4_096)) return false;
        var success = true;
        foreach (var pair in state)
        {
            var separator = pair.Key.LastIndexOf('.');
            if (separator < 0 || !Enum.TryParse<DefaultAudioRole>(pair.Key[(separator + 1)..], out var role))
            {
                success = false;
                continue;
            }

            success &= AudioPolicyConfig.SetDefaultEndpoint(pair.Value, role);
        }
        return success;
    }

    public static async Task<bool> RestoreLevelsBackupAsync(BackupEntry backup, CancellationToken ct)
    {
        if (!File.Exists(backup.Location)) return false;
        var levels = JsonSerializer.Deserialize<List<AudioLevelState>>(await File.ReadAllTextAsync(backup.Location, ct).ConfigureAwait(false)) ?? [];
        if (levels.Count is 0 or > 16 || levels.Any(item =>
                string.IsNullOrWhiteSpace(item.DeviceId) || item.DeviceId.Length > 4_096 ||
                !float.IsFinite(item.Level) || item.Level is < 0 or > 1)) return false;
        using var enumerator = new MMDeviceEnumerator();
        var success = true;
        foreach (var item in levels)
        {
            try
            {
                using var device = enumerator.GetDevice(item.DeviceId);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = item.Level;
                device.AudioEndpointVolume.Mute = item.Muted;
            }
            catch { success = false; }
        }
        return success;
    }

    private static async Task<BackupEntry?> CaptureAudioRegistryAndServicesAsync(
        FixContext context,
        string registryKey,
        string label,
        CancellationToken ct)
    {
        var directory = ModuleHelpers.BackupDirectory(context);
        var services = await context.Backups.CaptureServicesAsync(AudioServices, directory, ct).ConfigureAwait(false);
        var registry = await context.Backups.CaptureRegistryAsync(registryKey, directory, ct).ConfigureAwait(false);
        return services is not null && registry is not null
            ? await context.Backups.CaptureBundleAsync(label, [services, registry], directory, ct).ConfigureAwait(false)
            : null;
    }

    private static List<AudioLevelState> CaptureLevels() => CaptureLevels(AudioEndpointScope.All);

    private static List<AudioLevelState> CaptureLevels(AudioEndpointScope scope)
    {
        var result = new List<AudioLevelState>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in FlowsFor(scope))
        {
            try
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                result.Add(new AudioLevelState(device.ID, device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute));
            }
            catch { }
        }
        return result;
    }

    private static bool IsVirtualCompanionRunning(string token)
    {
        var processes = token switch
        {
            var value when value.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) => new[] { "voicemeeter", "voicemeeterpro", "voicemeeter8" },
            var value when value.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) => new[] { "NVIDIA Broadcast", "NVIDIA Broadcast UI" },
            var value when value.Contains("Discord", StringComparison.OrdinalIgnoreCase) => new[] { "Discord" },
            var value when value.Contains("Steam", StringComparison.OrdinalIgnoreCase) => new[] { "steam", "steamwebhelper" },
            _ => []
        };
        if (processes.Length == 0) return true;
        foreach (var name in processes)
        {
            var matches = Process.GetProcessesByName(name);
            try
            {
                if (matches.Length > 0) return true;
            }
            finally
            {
                foreach (var process in matches) process.Dispose();
            }
        }

        return false;
    }

    private static IEnumerable<string> DeviceSpecificTokens(string value)
    {
        var normalized = new string(value.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ').ToArray());
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audio", "bluetooth", "device", "hands", "free", "stereo", "headset",
            "speaker", "output", "eller", "serbest", "kulaklik", "hoparlor"
        };
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4 && !generic.Contains(token));
    }

    private static int DeviceMatchScore(string? candidateName, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(candidateName) || tokens.Count == 0) return 0;
        var normalized = new string(candidateName.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ').ToArray());
        return tokens
            .Where(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            .Sum(token => token.Length);
    }

    private static string DescribeDevices(IEnumerable<MMDevice> devices) =>
        string.Join(Environment.NewLine, devices.Select(device => $"{device.FriendlyName}: {device.State}; {device.ID}"));

    private static void DisposeDevices(IEnumerable<MMDevice> devices)
    {
        foreach (var device in devices) device.Dispose();
    }

    private static int GetInt(JsonElement element, string property) =>
        ModuleHelpers.TryGetPropertyIgnoreCase(element, property, out var value) &&
        (value.TryGetInt32(out var number) || int.TryParse(value.ToString(), out number))
            ? number
            : 0;

    private static double CalculatePeak(byte[] buffer, int bytes, WaveFormat format)
    {
        double peak = 0;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      format is WaveFormatExtensible extensible &&
                      extensible.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
        if (isFloat && format.BitsPerSample == 32)
        {
            for (var index = 0; index + 3 < bytes; index += 4)
            {
                var sample = BitConverter.ToSingle(buffer, index);
                if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var index = 0; index + 1 < bytes; index += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, index) / 32768d));
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (var index = 0; index + 2 < bytes; index += 3)
            {
                var sample = buffer[index] | buffer[index + 1] << 8 | buffer[index + 2] << 16;
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                peak = Math.Max(peak, Math.Abs(sample / 8_388_608d));
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (var index = 0; index + 3 < bytes; index += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buffer, index) / 2_147_483_648d));
            }
        }
        else if (format.BitsPerSample == 8)
        {
            for (var index = 0; index < bytes; index++)
            {
                peak = Math.Max(peak, Math.Abs((buffer[index] - 128) / 128d));
            }
        }
        return Math.Min(1, peak);
    }

    private sealed record AudioLevelState(string DeviceId, float Level, bool Muted);

    private sealed class StereoToneProvider : ISampleProvider
    {
        private double _phase;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        public int ActiveChannel { get; set; } = -1;
        public float Gain { get; set; } = .10f;
        public double Frequency { get; set; } = 740;

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / 2;
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = (float)(Math.Sin(_phase) * Gain);
                _phase += 2 * Math.PI * Frequency / WaveFormat.SampleRate;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                buffer[offset + frame * 2] = ActiveChannel is 0 or 2 ? sample : 0;
                buffer[offset + frame * 2 + 1] = ActiveChannel is 1 or 2 ? sample : 0;
            }
            return frames * 2;
        }
    }
}
