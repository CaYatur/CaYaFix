// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using CaYaFix.Core;

namespace CaYaFix.Modules.Shared;

internal sealed class DelegateDiagnosticCheck : DiagnosticCheck
{
    private readonly Func<DiagnosticContext, CancellationToken, Task<Finding?>> _run;

    public DelegateDiagnosticCheck(
        string id,
        string titleKey,
        string moduleId,
        Func<DiagnosticContext, CancellationToken, Task<Finding?>> run,
        bool quick = true,
        bool supportsPostRepairVerification = true)
    {
        Id = id;
        TitleKey = titleKey;
        ModuleId = moduleId;
        _run = run;
        IsQuickCheck = quick;
        SupportsPostRepairVerification = supportsPostRepairVerification;
    }

    public override string Id { get; }
    public override string TitleKey { get; }
    public override string ModuleId { get; }
    public override bool IsQuickCheck { get; }
    public override bool SupportsPostRepairVerification { get; }

    public override Task<Finding?> RunAsync(DiagnosticContext context, CancellationToken ct) => _run(context, ct);
}

internal sealed class DelegateFixAction : FixAction
{
    private readonly Func<FixContext, CancellationToken, Task<BackupEntry?>> _backup;
    private readonly Func<FixContext, CancellationToken, Task<FixResult>> _apply;
    private readonly Func<FixContext, CancellationToken, Task<bool>> _verify;
    private readonly Func<FixContext, BackupEntry, CancellationToken, Task<bool>>? _undo;

    public DelegateFixAction(
        string id,
        string titleKey,
        string moduleId,
        RiskTier tier,
        Func<FixContext, CancellationToken, Task<BackupEntry?>> backup,
        Func<FixContext, CancellationToken, Task<FixResult>> apply,
        Func<FixContext, CancellationToken, Task<bool>> verify,
        bool requiresReboot = false,
        bool requiresTarget = false,
        Func<FixContext, BackupEntry, CancellationToken, Task<bool>>? undo = null)
    {
        Id = id;
        TitleKey = titleKey;
        ModuleId = moduleId;
        Tier = tier;
        RequiresReboot = requiresReboot;
        RequiresTargetParameter = requiresTarget;
        PreviewSteps = RepairPreviewCatalog.Get(id);
        _backup = backup;
        _apply = apply;
        _verify = verify;
        _undo = undo;
    }

    public override string Id { get; }
    public override string TitleKey { get; }
    public override string ModuleId { get; }
    public override RiskTier Tier { get; }
    public override bool RequiresReboot { get; }
    public override bool RequiresTargetParameter { get; }
    public override IReadOnlyList<string> PreviewSteps { get; }

    public override Task<BackupEntry?> BackupAsync(FixContext context, CancellationToken ct) => _backup(context, ct);
    public override Task<FixResult> ApplyAsync(FixContext context, CancellationToken ct) => _apply(context, ct);
    public override Task<bool> VerifyAsync(FixContext context, CancellationToken ct) => _verify(context, ct);

    public override Task<bool> UndoAsync(FixContext context, BackupEntry backup, CancellationToken ct) =>
        _undo is null ? base.UndoAsync(context, backup, ct) : _undo(context, backup, ct);
}

internal sealed class DelegateLiveTest : LiveTest
{
    private readonly Func<DiagnosticContext, CancellationToken, IAsyncEnumerable<TestProgress>> _run;

    public DelegateLiveTest(
        string id,
        string titleKey,
        string moduleId,
        Func<DiagnosticContext, CancellationToken, IAsyncEnumerable<TestProgress>> run)
    {
        Id = id;
        TitleKey = titleKey;
        ModuleId = moduleId;
        _run = run;
    }

    public override string Id { get; }
    public override string TitleKey { get; }
    public override string ModuleId { get; }

    public override IAsyncEnumerable<TestProgress> RunAsync(DiagnosticContext context, CancellationToken ct) =>
        _run(context, ct);
}

internal static class ModuleHelpers
{
    public static string BackupDirectory(FixContext context)
    {
        var path = Path.Combine(context.Session.DirectoryPath, "Backups");
        Directory.CreateDirectory(path);
        return path;
    }

    public static async Task<BackupEntry?> TransientMarkerAsync(
        FixContext context,
        string label,
        CancellationToken ct)
    {
        var entry = await context.Backups.CaptureValueAsync(
            label,
            new { note = "Transient operation; no persistent state was changed before execution." },
            BackupDirectory(context),
            ct).ConfigureAwait(false);
        if (entry is not null)
        {
            entry.Metadata["restoreExecutable"] = "cmd.exe";
            entry.Metadata["restoreArguments"] = JsonSerializer.Serialize(new[] { "/d", "/c", "exit", "0" });
        }

        return entry;
    }

    /// <summary>
    /// Registry export when the key exists; a transient marker when it does not.
    /// Deleting values under a missing key is a no-op, so a marker is an honest backup.
    /// </summary>
    public static async Task<BackupEntry?> CaptureRegistryOrMarkerAsync(
        FixContext context,
        string key,
        string markerLabel,
        CancellationToken ct)
    {
        var registry = await context.Backups.CaptureRegistryAsync(key, BackupDirectory(context), ct)
            .ConfigureAwait(false);
        return registry ?? await TransientMarkerAsync(context, markerLabel, ct).ConfigureAwait(false);
    }

    public static Task<BackupEntry?> CapturePnpDeviceStateAsync(
        FixContext context,
        string label,
        string targetParameterKey,
        CancellationToken ct)
    {
        if (!context.Parameters.TryGetValue(targetParameterKey, out var target) ||
            string.IsNullOrWhiteSpace(target) || target.Length > 4_096 || target.IndexOf('\0') >= 0)
        {
            return Task.FromResult<BackupEntry?>(null);
        }

        var literal = target.Replace("'", "''", StringComparison.Ordinal);
        var capture = $"$device=Get-PnpDevice -InstanceId '{literal}' -ErrorAction Stop; if($null -eq $device){{throw 'Device not found'}}; $device|Select-Object InstanceId,Status,Problem|ConvertTo-Json -Compress";
        const string restore = "$state=Get-Content -Raw -LiteralPath '{backup}'|ConvertFrom-Json; if([string]::IsNullOrWhiteSpace([string]$state.InstanceId)){exit 2}; $tool=Join-Path $env:windir 'System32\\pnputil.exe'; if(-not (Test-Path -LiteralPath $tool -PathType Leaf)){exit 3}; $problem=[string]$state.Problem; if($problem -match 'DISABLED|(^|\\D)22(\\D|$)'){& $tool /disable-device $state.InstanceId}else{& $tool /enable-device $state.InstanceId}; if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}";
        return context.Backups.CaptureCommandStateAsync(
            label,
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", capture],
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", restore],
            BackupDirectory(context),
            ct);
    }

    public static async Task<FixResult> RunSequenceAsync(
        FixContext context,
        IEnumerable<CommandStep> steps,
        CancellationToken ct,
        string successKey = "FixResult_Applied")
    {
        var details = new List<string>();
        foreach (var step in steps)
        {
            var result = await context.Commands.RunAsync(step.Executable, step.Arguments, step.Timeout, ct)
                .ConfigureAwait(false);
            details.Add($"{step.Executable}: exit={result.ExitCode}");
            if (!result.Success && !step.AcceptedExitCodes.Contains(result.ExitCode))
            {
                return FixResult.Fail("FixResult_CommandFailed", string.Join(Environment.NewLine, details) + Environment.NewLine + result.StdErr);
            }
        }

        return FixResult.Ok(successKey, string.Join(Environment.NewLine, details));
    }

    public static async Task<bool> AllCommandsSucceedAsync(
        ICommandRunner commands,
        IEnumerable<CommandStep> steps,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var result = await commands.RunAsync(step.Executable, step.Arguments, step.Timeout, ct).ConfigureAwait(false);
            if (!result.Success && !step.AcceptedExitCodes.Contains(result.ExitCode))
            {
                return false;
            }
        }

        return true;
    }

    public static async Task<bool> IsServiceRunningAsync(
        FixContext context,
        string serviceName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 256 ||
            serviceName.Any(character => !char.IsLetterOrDigit(character) && character is not '_' and not '-'))
        {
            return false;
        }

        var root = await context.Commands.RunPsJsonAsync<JsonElement>(
            $"Get-CimInstance Win32_Service -Filter \"Name='{serviceName}'\" | Select-Object Name,State",
            ct).ConfigureAwait(false);
        var rows = Array(root).ToArray();
        return rows.Length == 1 && string.Equals(
            GetString(rows[0], "State"), "Running", StringComparison.OrdinalIgnoreCase);
    }

    public static Finding Finding(
        string checkId,
        string moduleId,
        Severity severity,
        string messageKey,
        string technicalDetail,
        params string[] fixIds) =>
        new()
        {
            CheckId = checkId,
            ModuleId = moduleId,
            Severity = severity,
            MessageKey = messageKey,
            TechnicalDetail = technicalDetail,
            RecommendedFixIds = fixIds
        };

    public static string? GetString(JsonElement element, string property)
    {
        if (!TryGetPropertyIgnoreCase(element, property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.ToString()
        };
    }

    public static bool TryGetPropertyIgnoreCase(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(property))
        {
            return false;
        }

        if (element.TryGetProperty(property, out value))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<JsonElement> Array(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : [];

    public static async IAsyncEnumerable<TestProgress> EmptyLiveTest(
        string id,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new TestProgress(id, "TestStage_NotAvailable", 1, string.Empty);
        await Task.CompletedTask;
    }
}

internal sealed record CommandStep(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    IReadOnlySet<int> AcceptedExitCodes)
{
    public CommandStep(string executable, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
        : this(executable, arguments, timeout ?? TimeSpan.FromMinutes(2), new HashSet<int>())
    {
    }
}
