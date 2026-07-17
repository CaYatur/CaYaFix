// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.ObjectModel;

namespace CaYaFix.Core;

public sealed class FixEngine
{
    private const int MaximumStoredDetailCharacters = 64 * 1024;
    private readonly ISessionManager _sessions;

    public FixEngine(ISessionManager sessions) => _sessions = sessions;

    public Task<IReadOnlyList<FixExecutionResult>> RunAsync(
        IEnumerable<(Finding Finding, FixAction Fix)> requested,
        FixContext context,
        IProgress<FixProgress>? progress,
        CancellationToken ct) =>
        RunAsync(
            requested.Select(item => new FixRequest(item.Finding, item.Fix, context.Parameters)),
            context,
            progress,
            ct);

    public async Task<IReadOnlyList<FixExecutionResult>> RunAsync(
        IEnumerable<FixRequest> requested,
        FixContext context,
        IProgress<FixProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(context);
        var all = requested
            .Select(item => item with
            {
                Parameters = CopyParameters(item.Parameters ?? context.Parameters)
            })
            .GroupBy(item => $"{item.Finding.CheckId}\0{item.Fix.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var queue = all
            .Where(item => !item.Fix.RequiresReboot)
            .OrderBy(item => item.Fix.Tier)
            .Concat(all.Where(item => item.Fix.RequiresReboot).OrderBy(item => item.Fix.Tier))
            .ToArray();

        var results = new List<FixExecutionResult>(queue.Length);
        context.Session.Status = SessionStatus.Running;
        context.Session.CompletedAt = null;
        try
        {
            await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);

            for (var index = 0; index < queue.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var request = queue[index];
                var finding = request.Finding;
                var fix = request.Fix;
                var executionContext = WithParameters(context, request.Parameters);
            if (finding.Status == FindingStatus.Resolved)
            {
                progress?.Report(new FixProgress(
                    fix.Id,
                    fix.TitleKey,
                    "FixStage_Done",
                    index + 1,
                    queue.Length,
                    true));
                continue;
            }

            finding.Status = FindingStatus.Repairing;

            progress?.Report(new FixProgress(
                fix.Id,
                fix.TitleKey,
                "FixStage_Preparing",
                index,
                queue.Length));

            if (fix.Tier == RiskTier.Aggressive &&
                (!executionContext.ForceModeConfirmed || !executionContext.RestorePointAvailable))
            {
                var blocked = Record(
                    finding,
                    fix,
                    applied: false,
                    verified: false,
                    backup: null,
                    "FixResult_ForceRequirementsMissing");
                results.Add(blocked);
                finding.Status = FindingStatus.Skipped;
                AddToSession(context.Session, blocked);
                await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                continue;
            }

            if (fix.RequiresTargetParameter &&
                (!executionContext.Parameters.TryGetValue($"{fix.Id}.target", out var targetParameter) ||
                 string.IsNullOrWhiteSpace(targetParameter)))
            {
                var noTarget = Record(
                    finding,
                    fix,
                    applied: false,
                    verified: false,
                    backup: null,
                    "FixResult_TargetRequired");
                results.Add(noTarget);
                finding.Status = FindingStatus.Skipped;
                AddToSession(context.Session, noTarget);
                await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                continue;
            }

            if (executionContext.DryRun)
            {
                WritePreview(executionContext, fix);
                var preview = Record(
                    finding,
                    fix,
                    applied: false,
                    verified: false,
                    backup: null,
                    "FixResult_DryRun");
                results.Add(preview);
                finding.Status = FindingStatus.Skipped;
                AddToSession(context.Session, preview);
                await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                continue;
            }

            progress?.Report(new FixProgress(
                fix.Id,
                fix.TitleKey,
                "FixStage_BackingUp",
                index,
                queue.Length));

            BackupEntry? backup = null;
            string backupFailureDetail = string.Empty;
            try
            {
                backup = await fix.BackupAsync(executionContext, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                backup = null;
                backupFailureDetail = ex.ToString();
            }

            var backupOptional = false;
            if (backup is null)
            {
                var allowBackupless = fix.Tier == RiskTier.Aggressive &&
                                      executionContext.ForceModeConfirmed &&
                                      executionContext.AllowBackuplessAggressive;
                if (!allowBackupless)
                {
                    var stopped = Record(
                        finding,
                        fix,
                        false,
                        false,
                        null,
                        "FixResult_BackupFailed",
                        backupFailureDetail);
                    results.Add(stopped);
                    finding.Status = FindingStatus.Unresolved;
                    AddToSession(context.Session, stopped);
                    await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                    continue;
                }

                backupOptional = true;
                executionContext.Console.Write(new ConsoleLine(
                    DateTimeOffset.Now,
                    "WARN",
                    "Aggressive repair continuing without a recoverable action backup (explicit consent)."));
            }

            var recoveryIntent = AddRecoveryIntent(context.Session, finding, fix, backup, backupOptional);
            await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);

            progress?.Report(new FixProgress(
                fix.Id,
                fix.TitleKey,
                "FixStage_Applying",
                index,
                queue.Length));

            FixResult applied;
            try
            {
                applied = await fix.ApplyAsync(executionContext, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var rolledBack = await TryRollbackAsync(fix, executionContext, backup).ConfigureAwait(false);
                var cancelled = Record(
                    finding,
                    fix,
                    !rolledBack,
                    false,
                    backup,
                    rolledBack ? "FixResult_CancelledRolledBack" : "FixResult_CancelledRollbackFailed");
                finding.Status = FindingStatus.Unresolved;
                UpdateSessionAction(recoveryIntent, cancelled);
                context.Session.Status = context.Session.PendingVerify
                    ? SessionStatus.PendingVerification
                    : SessionStatus.Cancelled;
                if (!context.Session.PendingVerify) context.Session.CompletedAt = DateTimeOffset.Now;
                await TryPersistTerminalStateAsync(context.Session).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                applied = FixResult.Fail("FixResult_ApplyFailed", ex.ToString());
            }

            if (!applied.Success)
            {
                var rolledBack = await TryRollbackAsync(fix, executionContext, backup).ConfigureAwait(false);
                var failed = Record(
                    finding,
                    fix,
                    !rolledBack,
                    false,
                    backup,
                    rolledBack
                        ? "FixResult_ApplyFailedRolledBack"
                        : backup is not null
                            ? "FixResult_ApplyFailedRollbackFailed"
                            : applied.MessageKey,
                    applied.TechnicalDetail);
                results.Add(failed);
                finding.Status = FindingStatus.Unresolved;
                UpdateSessionAction(recoveryIntent, failed);
                await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                continue;
            }

            var needsReboot = fix.RequiresReboot || applied.RequiresReboot;
            var verified = false;
            if (!needsReboot)
            {
                progress?.Report(new FixProgress(
                    fix.Id,
                    fix.TitleKey,
                    "FixStage_Verifying",
                    index,
                    queue.Length));
                try
                {
                    verified = await fix.VerifyAsync(executionContext, ct).ConfigureAwait(false);
                    if (verified && request.DiagnosticCheck is not null)
                    {
                        verified = await request.DiagnosticCheck.RunAsync(
                            new DiagnosticContext
                            {
                                Commands = executionContext.Commands,
                                Text = executionContext.Text,
                                Thresholds = executionContext.Thresholds,
                                SessionDirectory = executionContext.Session.DirectoryPath
                            },
                            ct).ConfigureAwait(false) is null;
                    }
                }
                catch (OperationCanceledException)
                {
                    var rolledBack = await TryRollbackAsync(fix, executionContext, backup).ConfigureAwait(false);
                    var cancelled = Record(
                        finding,
                        fix,
                        !rolledBack,
                        false,
                        backup,
                        rolledBack ? "FixResult_CancelledRolledBack" : "FixResult_CancelledRollbackFailed",
                        applied.TechnicalDetail);
                    finding.Status = FindingStatus.Unresolved;
                    UpdateSessionAction(recoveryIntent, cancelled);
                    context.Session.Status = context.Session.PendingVerify
                        ? SessionStatus.PendingVerification
                        : SessionStatus.Cancelled;
                    if (!context.Session.PendingVerify) context.Session.CompletedAt = DateTimeOffset.Now;
                    await TryPersistTerminalStateAsync(context.Session).ConfigureAwait(false);
                    throw;
                }
                catch
                {
                    verified = false;
                }
            }

            if (!needsReboot && !verified)
            {
                var rolledBack = await TryRollbackAsync(fix, executionContext, backup).ConfigureAwait(false);
                finding.Status = FindingStatus.Unresolved;
                var failedVerification = Record(
                    finding,
                    fix,
                    !rolledBack,
                    false,
                    backup,
                    rolledBack
                        ? "FixResult_VerificationFailedRolledBack"
                        : backup is not null
                            ? "FixResult_VerificationFailedRollbackFailed"
                            : "FixResult_NotVerified",
                    applied.TechnicalDetail);
                results.Add(failedVerification);
                UpdateSessionAction(recoveryIntent, failedVerification);
                await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                progress?.Report(new FixProgress(
                    fix.Id,
                    fix.TitleKey,
                    rolledBack ? "FixStage_RolledBack" : "FixStage_RollbackFailed",
                    index + 1,
                    queue.Length,
                    false));
                continue;
            }

            finding.Status = needsReboot
                ? FindingStatus.Repairing
                : verified
                    ? FindingStatus.Resolved
                    : FindingStatus.Unresolved;

            var completed = Record(
                finding,
                fix,
                true,
                verified,
                backup,
                needsReboot ? "FixResult_PendingReboot" : verified ? "FixResult_Verified" : "FixResult_NotVerified",
                applied.TechnicalDetail,
                needsReboot);
            results.Add(completed);
            UpdateSessionAction(recoveryIntent, completed);

            if (needsReboot)
            {
                context.Session.RequiresReboot = true;
                context.Session.PendingVerify = true;
                context.Session.Status = SessionStatus.PendingVerification;
            }

            await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
                progress?.Report(new FixProgress(
                    fix.Id,
                    fix.TitleKey,
                    needsReboot ? "FixStage_PendingReboot" : "FixStage_Done",
                    index + 1,
                    queue.Length,
                    verified));
            }

            if (!context.Session.PendingVerify)
            {
                context.Session.Status = SessionStatus.Completed;
                context.Session.CompletedAt = DateTimeOffset.Now;
            }

            await _sessions.SaveAsync(context.Session, ct).ConfigureAwait(false);
            return results;
        }
        catch (OperationCanceledException)
        {
            ResetRepairingFindings(queue, context.Session);
            if (!context.Session.PendingVerify)
            {
                context.Session.Status = SessionStatus.Cancelled;
                context.Session.CompletedAt = DateTimeOffset.Now;
            }
            await TryPersistTerminalStateAsync(context.Session).ConfigureAwait(false);
            throw;
        }
        catch
        {
            ResetRepairingFindings(queue, context.Session);
            if (!context.Session.PendingVerify)
            {
                context.Session.Status = SessionStatus.Failed;
                context.Session.CompletedAt = DateTimeOffset.Now;
            }
            await TryPersistTerminalStateAsync(context.Session).ConfigureAwait(false);
            throw;
        }
    }

    private static FixContext WithParameters(
        FixContext source,
        IReadOnlyDictionary<string, string>? parameters) => new()
    {
        Commands = source.Commands,
        Backups = source.Backups,
        Console = source.Console,
        Text = source.Text,
        Session = source.Session,
        DryRun = source.DryRun,
        ForceModeConfirmed = source.ForceModeConfirmed,
        RestorePointAvailable = source.RestorePointAvailable,
        AllowBackuplessAggressive = source.AllowBackuplessAggressive,
        Thresholds = source.Thresholds,
        Parameters = CopyParameters(parameters ?? source.Parameters)
    };

    private static IReadOnlyDictionary<string, string> CopyParameters(
        IReadOnlyDictionary<string, string> source)
    {
        if (source.Count > 64 || source.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 || pair.Key.IndexOf('\0') >= 0 ||
                pair.Value is null || pair.Value.Length > 4_096 || pair.Value.IndexOf('\0') >= 0))
        {
            throw new InvalidOperationException("Repair parameters exceed the supported safety limits.");
        }

        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new InvalidOperationException("Repair parameter names must be unique without regard to case.");
            }
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static void WritePreview(FixContext context, FixAction fix)
    {
        context.Console.Write(new ConsoleLine(
            DateTimeOffset.Now,
            "PLAN",
            $"{context.Text.Get(fix.TitleKey)} [{fix.Id}]"));
        var steps = fix.PreviewSteps.Take(64).ToArray();
        if (steps.Length == 0)
        {
            context.Console.Write(new ConsoleLine(DateTimeOffset.Now, "PLAN", "No persistent change will be executed in preview mode."));
            return;
        }

        foreach (var step in steps)
        {
            var bounded = string.IsNullOrWhiteSpace(step)
                ? "[empty preview step]"
                : step.Length <= 4_096
                    ? step
                    : string.Concat(step.AsSpan(0, 4_095), "…");
            context.Console.Write(new ConsoleLine(DateTimeOffset.Now, "PLAN", bounded));
        }
    }

    private async Task TryPersistTerminalStateAsync(SessionManifest session)
    {
        try
        {
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Persistence failure must not mask the original cancellation or execution error.
        }
    }

    private static void ResetRepairingFindings(IEnumerable<FixRequest> requests, SessionManifest session)
    {
        var pendingChecks = session.Actions
            .Where(action => action.Applied && action.RequiresReboot && !action.Verified && !action.Undone)
            .Select(action => action.FindingCheckId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in requests.Select(request => request.Finding).Distinct())
        {
            if (finding.Status == FindingStatus.Repairing && !pendingChecks.Contains(finding.CheckId))
            {
                finding.Status = FindingStatus.Unresolved;
            }
        }
    }

    private static async Task<bool> TryRollbackAsync(
        FixAction fix,
        FixContext context,
        BackupEntry? backup)
    {
        if (backup is null) return false;
        try
        {
            return await fix.UndoAsync(context, backup, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static FixExecutionResult Record(
        Finding finding,
        FixAction fix,
        bool applied,
        bool verified,
        BackupEntry? backup,
        string messageKey,
        string detail = "",
        bool? reboot = null) =>
        new(
            fix.Id,
            fix.TitleKey,
            finding.CheckId,
            fix.Tier,
            applied,
            verified,
            backup is not null,
            reboot ?? fix.RequiresReboot,
            messageKey,
            backup,
            BoundStoredDetail(detail));

    private static void AddToSession(SessionManifest session, FixExecutionResult result)
    {
        session.Actions.Add(new SessionActionRecord
        {
            FixId = result.FixId,
            TitleMessageKey = result.TitleKey,
            FindingCheckId = result.FindingCheckId,
            Tier = result.Tier,
            Applied = result.Applied,
            Verified = result.Verified,
            RequiresReboot = result.RequiresReboot,
            BackupOptional = result.Applied && result.Backup is null && result.Tier == RiskTier.Aggressive,
            ResultMessageKey = result.MessageKey,
            TechnicalDetail = result.TechnicalDetail,
            Backup = result.Backup
        });
    }

    private static SessionActionRecord AddRecoveryIntent(
        SessionManifest session,
        Finding finding,
        FixAction fix,
        BackupEntry? backup,
        bool backupOptional = false)
    {
        var action = new SessionActionRecord
        {
            FixId = fix.Id,
            TitleMessageKey = fix.TitleKey,
            FindingCheckId = finding.CheckId,
            Tier = fix.Tier,
            Applied = true,
            Verified = false,
            RequiresReboot = fix.RequiresReboot,
            BackupOptional = backupOptional,
            ResultMessageKey = "FixResult_ApplyInProgress",
            Backup = backup
        };
        session.Actions.Add(action);
        return action;
    }

    private static void UpdateSessionAction(
        SessionActionRecord action,
        FixExecutionResult result)
    {
        action.Applied = result.Applied;
        action.Verified = result.Verified;
        action.Undone = false;
        action.RequiresReboot = result.RequiresReboot;
        action.BackupOptional = result.Applied && result.Backup is null && result.Tier == RiskTier.Aggressive;
        action.ResultMessageKey = result.MessageKey;
        action.TechnicalDetail = result.TechnicalDetail;
        action.Backup = result.Backup;
    }

    private static string BoundStoredDetail(string? value)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ');
        return normalized.Length <= MaximumStoredDetailCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaximumStoredDetailCharacters - 25), "\n[truncated by CaYaFix]");
    }
}
