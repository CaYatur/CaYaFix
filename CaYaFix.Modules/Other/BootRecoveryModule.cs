// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;
using CaYaFix.Modules.Shared;

namespace CaYaFix.Modules.Other;

/// <summary>
/// Online boot / recovery-environment health based on Microsoft recovery guidance:
/// reagentc (WinRE), bcdedit (BCD store), bcdboot (boot files).
/// Destructive bootrec rebuilds remain WinRE-only and are intentionally not automated online.
/// </summary>
public sealed class BootRecoveryModule : WindowsModuleBase
{
    private const string Id = "boot";

    public BootRecoveryModule() : base(
        new ModuleInfo(Id, "Module_Boot_Name", "Module_Boot_Description", "recovery.svg", 14),
        CreateChecks(),
        CreateFixes(),
        [
            new Playbook(
                "boot.recovery-missing",
                Id,
                "Symptom_Boot_RecoveryMissing",
                ["boot.winre", "boot.bcd"],
                ["boot.export-bcd", "boot.enable-winre", "boot.recoveryenabled"]),
            new Playbook(
                "boot.config-suspect",
                Id,
                "Symptom_Boot_ConfigSuspect",
                ["boot.bcd", "boot.bcd-firmware", "boot.winre"],
                ["boot.export-bcd", "boot.rebuild-bcdboot", "boot.enable-winre"])
        ])
    {
    }

    private static IReadOnlyList<DiagnosticCheck> CreateChecks() =>
    [
        new DelegateDiagnosticCheck("boot.winre", "Check_Boot_WinRe", Id, async (context, ct) =>
        {
            var result = await context.Commands.RunAsync(
                "reagentc.exe",
                ["/info"],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            var output = result.StdOut + Environment.NewLine + result.StdErr;
            if (!result.Success && string.IsNullOrWhiteSpace(output))
            {
                return ModuleHelpers.Finding(
                    "boot.winre",
                    Id,
                    Severity.Warning,
                    "Finding_Boot_WinReUnreadable",
                    output,
                    "boot.enable-winre",
                    "boot.export-bcd");
            }

            var disabled = output.Contains("Disabled", StringComparison.OrdinalIgnoreCase) &&
                           !output.Contains("Enabled", StringComparison.OrdinalIgnoreCase);
            // Prefer explicit status line when present.
            if (output.Contains("Windows RE status", StringComparison.OrdinalIgnoreCase))
            {
                disabled = output.Contains("Windows RE status", StringComparison.OrdinalIgnoreCase) &&
                           output.Contains("Disabled", StringComparison.OrdinalIgnoreCase);
            }

            return disabled
                ? ModuleHelpers.Finding(
                    "boot.winre",
                    Id,
                    Severity.Warning,
                    "Finding_Boot_WinReDisabled",
                    output.Trim(),
                    "boot.enable-winre",
                    "boot.export-bcd",
                    "boot.recoveryenabled")
                : null;
        }),
        new DelegateDiagnosticCheck("boot.bcd", "Check_Boot_Bcd", Id, async (context, ct) =>
        {
            var result = await context.Commands.RunAsync(
                "bcdedit.exe",
                ["/enum", "{current}"],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            var output = result.StdOut + Environment.NewLine + result.StdErr;
            if (result.Success &&
                output.Contains("identifier", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ModuleHelpers.Finding(
                "boot.bcd",
                Id,
                Severity.Critical,
                "Finding_Boot_BcdUnreadable",
                output.Trim(),
                "boot.export-bcd",
                "boot.rebuild-bcdboot",
                "boot.enable-winre");
        }),
        new DelegateDiagnosticCheck("boot.bcd-firmware", "Check_Boot_BcdFirmware", Id, async (context, ct) =>
        {
            // UEFI firmware boot manager enumeration (when available).
            var result = await context.Commands.RunAsync(
                "bcdedit.exe",
                ["/enum", "firmware"],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            var output = result.StdOut + Environment.NewLine + result.StdErr;
            if (result.Success)
            {
                return null;
            }

            // On legacy BIOS systems firmware enum can legitimately fail — only warn when BCD itself is also unhealthy.
            var current = await context.Commands.RunAsync(
                "bcdedit.exe",
                ["/enum", "{current}"],
                TimeSpan.FromMinutes(2),
                ct).ConfigureAwait(false);
            if (current.Success)
            {
                return null;
            }

            return ModuleHelpers.Finding(
                "boot.bcd-firmware",
                Id,
                Severity.Warning,
                "Finding_Boot_FirmwareEnumFailed",
                output.Trim(),
                "boot.export-bcd",
                "boot.rebuild-bcdboot");
        }, quick: false, supportsPostRepairVerification: false)
    ];

    private static IReadOnlyList<FixAction> CreateFixes() =>
    [
        new DelegateFixAction(
            "boot.export-bcd",
            "Fix_Boot_ExportBcd",
            Id,
            RiskTier.Safe,
            (context, ct) => ModuleHelpers.TransientMarkerAsync(context, "boot-export-bcd", ct),
            async (context, ct) =>
            {
                var path = Path.Combine(ModuleHelpers.BackupDirectory(context), $"bcd-export-{DateTime.UtcNow:yyyyMMddHHmmss}.bcd");
                var result = await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/export", path],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                return result.Success && File.Exists(path)
                    ? FixResult.Ok("FixResult_Applied", $"BCD exported to {path}")
                    : FixResult.Fail("FixResult_CommandFailed", result.StdOut + result.StdErr);
            },
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/enum", "{current}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success),
        new DelegateFixAction(
            "boot.recoveryenabled",
            "Fix_Boot_RecoveryEnabled",
            Id,
            RiskTier.Safe,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var exportPath = Path.Combine(dir, "bcd-before-recoveryenabled.bcd");
                await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/export", exportPath],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                return await ModuleHelpers.TransientMarkerAsync(context, "boot-recoveryenabled", ct)
                    .ConfigureAwait(false);
            },
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                new CommandStep("bcdedit.exe", ["/set", "{current}", "recoveryenabled", "Yes"]),
                new CommandStep("bcdedit.exe", ["/set", "{current}", "bootstatuspolicy", "DisplayAllFailures"])
                {
                    AcceptedExitCodes = new HashSet<int> { 0, 1 }
                }
            ], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/enum", "{current}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                return result.Success &&
                       result.StdOut.Contains("recoveryenabled", StringComparison.OrdinalIgnoreCase);
            }),
        new DelegateFixAction(
            "boot.enable-winre",
            "Fix_Boot_EnableWinRe",
            Id,
            RiskTier.Moderate,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var exportPath = Path.Combine(dir, "bcd-before-winre.bcd");
                await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/export", exportPath],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                return await ModuleHelpers.TransientMarkerAsync(context, "boot-enable-winre", ct)
                    .ConfigureAwait(false);
            },
            (context, ct) => ModuleHelpers.RunSequenceAsync(context,
            [
                // Microsoft: reagentc /enable restores WinRE when the recovery image is present.
                new CommandStep("reagentc.exe", ["/enable"], TimeSpan.FromMinutes(5))
            ], ct),
            async (context, ct) =>
            {
                var result = await context.Commands.RunAsync(
                    "reagentc.exe",
                    ["/info"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                var output = result.StdOut + result.StdErr;
                return output.Contains("Enabled", StringComparison.OrdinalIgnoreCase);
            }),
        new DelegateFixAction(
            "boot.rebuild-bcdboot",
            "Fix_Boot_RebuildBcdBoot",
            Id,
            RiskTier.Aggressive,
            async (context, ct) =>
            {
                var dir = ModuleHelpers.BackupDirectory(context);
                var exportPath = Path.Combine(dir, "bcd-before-bcdboot.bcd");
                var export = await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/export", exportPath],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false);
                if (!export.Success)
                {
                    return null;
                }

                return await ModuleHelpers.TransientMarkerAsync(context, "boot-rebuild-bcdboot", ct)
                    .ConfigureAwait(false);
            },
            async (context, ct) =>
            {
                // Microsoft BCDBoot: copy boot files from the Windows directory to the system partition.
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrWhiteSpace(windows) || !Directory.Exists(windows))
                {
                    return FixResult.Fail("FixResult_CommandFailed", "Windows directory could not be resolved.");
                }

                var result = await context.Commands.RunAsync(
                    "bcdboot.exe",
                    [windows, "/f", "ALL"],
                    TimeSpan.FromMinutes(5),
                    ct).ConfigureAwait(false);
                return result.Success
                    ? FixResult.Ok("FixResult_Applied", result.StdOut)
                    : FixResult.Fail("FixResult_CommandFailed", result.StdOut + result.StdErr);
            },
            async (context, ct) =>
                (await context.Commands.RunAsync(
                    "bcdedit.exe",
                    ["/enum", "{current}"],
                    TimeSpan.FromMinutes(2),
                    ct).ConfigureAwait(false)).Success,
            requiresReboot: true)
    ];
}
