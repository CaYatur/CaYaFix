// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Core;

public enum RestorePointCreateStatus
{
    Created,
    AlreadyPresent,
    Unavailable,
    Failed,
    TimedOut,
    Cancelled
}

public readonly record struct RestorePointCreateResult(
    RestorePointCreateStatus Status,
    string Detail)
{
    public bool IsUsable =>
        Status is RestorePointCreateStatus.Created or RestorePointCreateStatus.AlreadyPresent;
}

public sealed class RestorePointService
{
    private static readonly TimeSpan ProtectionCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecentPointCheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(90);

    private readonly ICommandRunner _commands;

    public RestorePointService(ICommandRunner commands) => _commands = commands;

    public async Task<bool> IsSystemProtectionAvailableAsync(CancellationToken ct)
    {
        const string command =
            "Get-CimInstance -Namespace root/default -ClassName SystemRestore -ErrorAction Stop | Select-Object -First 1 __CLASS";
        var result = await _commands.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
            ProtectionCheckTimeout,
            ct).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<RestorePointCreateResult> CreateAsync(string description, CancellationToken ct)
    {
        var safeDescription = new string(description
            .Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_')
            .Take(80)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeDescription))
        {
            safeDescription = "CaYaFix";
        }

        // Windows rate-limits Checkpoint-Computer; a restore point from the last day is enough.
        const string recentCheckCommand =
            "$ErrorActionPreference='Stop'; " +
            "Write-Host 'CaYaFix: checking for a recent System Restore point...'; " +
            "try { " +
            "  $points = @(Get-ComputerRestorePoint -ErrorAction Stop); " +
            "  $cutoff = (Get-Date).AddHours(-24); " +
            "  $recent = $points | Where-Object { " +
            "    $created = $null; " +
            "    if ($_.CreationTime -is [datetime]) { $created = $_.CreationTime } " +
            "    elseif ($_.CreationTime -match '^(\\d{14})') { " +
            "      $created = [datetime]::ParseExact($Matches[1], 'yyyyMMddHHmmss', $null) " +
            "    }; " +
            "    $null -ne $created -and $created -gt $cutoff " +
            "  } | Select-Object -First 1; " +
            "  if ($null -ne $recent) { Write-Host 'CaYaFix: recent restore point found.'; Write-Output 'RECENT'; exit 0 } " +
            "  Write-Host 'CaYaFix: no restore point from the last 24 hours.'; Write-Output 'NONE'; exit 0 " +
            "} catch { Write-Host ('CaYaFix: recent restore check skipped: ' + $_.Exception.Message); Write-Output 'NONE'; exit 0 }";

        var recentCheck = await _commands.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", recentCheckCommand],
            RecentPointCheckTimeout,
            ct).ConfigureAwait(false);

        if (recentCheck.TimedOut)
        {
            return new RestorePointCreateResult(
                RestorePointCreateStatus.TimedOut,
                "Checking for an existing restore point timed out.");
        }

        if (recentCheck.Success &&
            recentCheck.StdOut.Contains("RECENT", StringComparison.OrdinalIgnoreCase))
        {
            return new RestorePointCreateResult(
                RestorePointCreateStatus.AlreadyPresent,
                "A System Restore point from the last 24 hours is already available.");
        }

        var createCommand =
            "$ErrorActionPreference='Stop'; " +
            "Write-Host 'CaYaFix: creating System Restore point (this can take up to 90 seconds)...'; " +
            $"Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS; " +
            "Write-Host 'CaYaFix: System Restore point created successfully.'";

        var result = await _commands.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", createCommand],
            CreateTimeout,
            ct).ConfigureAwait(false);

        if (result.TimedOut)
        {
            return new RestorePointCreateResult(
                RestorePointCreateStatus.TimedOut,
                $"Creating a restore point timed out after {CreateTimeout.TotalSeconds:0} seconds.");
        }

        if (result.Success)
        {
            return new RestorePointCreateResult(
                RestorePointCreateStatus.Created,
                "System Restore point created.");
        }

        var detail = string.IsNullOrWhiteSpace(result.StdErr)
            ? $"Restore point creation failed (exit {result.ExitCode})."
            : result.StdErr.Trim();
        return new RestorePointCreateResult(RestorePointCreateStatus.Failed, detail);
    }
}
