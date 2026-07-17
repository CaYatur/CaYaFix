// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using CaYaFix.Core;

namespace CaYaFix.Modules.Shared;

/// <summary>
/// Shared builders for Microsoft-documented multi-method repairs used across thin modules.
/// </summary>
internal static class ExpandedRepairHelpers
{
    public static DelegateFixAction RestartNamedServices(
        string fixId,
        string titleKey,
        string moduleId,
        string[] serviceNames,
        string verifyService,
        RiskTier tier = RiskTier.Safe)
    {
        var namesLiteral = string.Join("','", serviceNames);
        var script =
            "$ErrorActionPreference='Continue'; " +
            "$names=@('" + namesLiteral + "'); " +
            "foreach($n in $names){ " +
            "  try{ " +
            "    $s=Get-Service -Name $n -ErrorAction Stop; " +
            "    if($s.StartType -eq 'Disabled'){ Set-Service -Name $n -StartupType Manual -ErrorAction SilentlyContinue }; " +
            "    if($s.Status -eq 'Running'){ Restart-Service -Name $n -Force -ErrorAction SilentlyContinue } " +
            "    else { Start-Service -Name $n -ErrorAction SilentlyContinue }; " +
            "    Start-Sleep -Milliseconds 300 " +
            "  }catch{} " +
            "}; exit 0";

        return new(
            fixId,
            titleKey,
            moduleId,
            tier,
            (context, ct) => context.Backups.CaptureServicesAsync(serviceNames, ModuleHelpers.BackupDirectory(context), ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", script],
                    TimeSpan.FromMinutes(4))
            ], ct),
            (context, ct) => ModuleHelpers.IsServiceRunningAsync(context, verifyService, ct));
    }

    public static DelegateFixAction PnpScanDevices(string fixId, string titleKey, string moduleId) =>
        new(
            fixId,
            titleKey,
            moduleId,
            RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, fixId, ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
                [new CommandStep("pnputil.exe", ["/scan-devices"], TimeSpan.FromMinutes(5))], ct),
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "pnputil.exe",
                    ["/enum-devices"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success);

    public static DelegateFixAction EnableDisabledPnp(
        string fixId,
        string titleKey,
        string moduleId,
        string classFilterPowerShell,
        string nameMatchRegex)
    {
        var script =
            "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
            "$namePattern='" + nameMatchRegex.Replace("'", "''") + "'; " +
            "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { " +
            "  (" + classFilterPowerShell + " -or $_.FriendlyName -match $namePattern) -and " +
            "  ($_.Status -eq 'Error' -or $_.Problem -match 'CM_PROB_DISABLED|22|DISABLED' -or $_.Status -eq 'Unknown') " +
            "} | ForEach-Object { & $tool /enable-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 200 }; exit 0";

        return new(
            fixId,
            titleKey,
            moduleId,
            RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, fixId, ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", script],
                    TimeSpan.FromMinutes(6))
            ], ct),
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "pnputil.exe",
                    ["/scan-devices"],
                    TimeSpan.FromMinutes(5),
                    ct).ConfigureAwait(false)).Success);
    }

    public static DelegateFixAction RestartPnpByClass(
        string fixId,
        string titleKey,
        string moduleId,
        string className,
        int maxDevices = 8)
    {
        var script =
            "$ErrorActionPreference='Continue'; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; " +
            "$n=0; Get-PnpDevice -Class '" + className + "' -PresentOnly -ErrorAction SilentlyContinue | " +
            "Where-Object Status -eq 'OK' | Select-Object -First " + maxDevices + " | ForEach-Object { " +
            "  & $tool /restart-device $_.InstanceId | Out-Null; Start-Sleep -Milliseconds 350; $n++ " +
            "}; exit 0";

        return new(
            fixId,
            titleKey,
            moduleId,
            RiskTier.Moderate,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, fixId, ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command", script],
                    TimeSpan.FromMinutes(8))
            ], ct),
            async (context, ct) =>
            {
                var root = await context.Commands.RunPsJsonAsync<JsonElement>(
                    "Get-PnpDevice -Class '" + className + "' -PresentOnly -ErrorAction SilentlyContinue | Select-Object Status",
                    ct).ConfigureAwait(false);
                return ModuleHelpers.Array(root).Any(row =>
                    string.Equals(ModuleHelpers.GetString(row, "Status"), "OK", StringComparison.OrdinalIgnoreCase));
            });
    }

    public static DelegateFixAction TransientCommand(
        string fixId,
        string titleKey,
        string moduleId,
        RiskTier tier,
        CommandStep[] steps,
        Func<FixContext, CancellationToken, Task<bool>> verify,
        bool requiresReboot = false) =>
        new(
            fixId,
            titleKey,
            moduleId,
            tier,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, fixId, ct),
            (context, ct) => ModuleHelpers.RunSequenceAsync(context, steps, ct),
            verify,
            requiresReboot: requiresReboot);
}
