// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;

namespace CaYaFix.Tests;

public sealed class FixEngineTests
{
    [Fact]
    public async Task BackupFailureStopsApply()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction("safe", RiskTier.Safe, events, backupSucceeds: false);

        var results = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)],
            Context(manager, dryRun: false),
            null,
            CancellationToken.None);

        Assert.Equal(["safe:backup"], events);
        Assert.False(results.Single().Applied);
        Assert.Equal("FixResult_BackupFailed", results.Single().MessageKey);
    }

    [Fact]
    public async Task OrdersByTierAndMovesRebootActionsLast()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var moderate = new StubFixAction("moderate", RiskTier.Moderate, events);
        var rebootSafe = new StubFixAction("reboot-safe", RiskTier.Safe, events, requiresReboot: true);
        var safe = new StubFixAction("safe", RiskTier.Safe, events);

        var results = await new FixEngine(manager).RunAsync(
            [(Finding("one"), moderate), (Finding("two"), rebootSafe), (Finding("three"), safe)],
            Context(manager, dryRun: false),
            null,
            CancellationToken.None);

        Assert.Equal(
            ["safe:backup", "safe:apply", "safe:verify", "moderate:backup", "moderate:apply", "moderate:verify", "reboot-safe:backup", "reboot-safe:apply"],
            events);
        Assert.Equal(["safe", "moderate", "reboot-safe"], results.Select(result => result.FixId));
        Assert.True(results.Last().RequiresReboot);
    }

    [Fact]
    public async Task DryRunDoesNotBackupOrApply()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction("preview", RiskTier.Safe, events);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], Context(manager, dryRun: true), null, CancellationToken.None);

        Assert.Empty(events);
        Assert.Equal("FixResult_DryRun", result.Single().MessageKey);
    }

    [Fact]
    public async Task DryRunWritesTheBoundedRepairPlanToTheConsole()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var console = new EventConsoleSink();
        var fix = new StubFixAction(
            "preview",
            RiskTier.Safe,
            events,
            previewSteps: ["first planned command", "second planned operation"]);

        await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)],
            Context(manager, dryRun: true, console: console),
            null,
            CancellationToken.None);

        Assert.Empty(events);
        Assert.Contains(console.Snapshot(), line => line.Level == "PLAN" && line.Text.Contains("first planned command"));
        Assert.Contains(console.Snapshot(), line => line.Level == "PLAN" && line.Text.Contains("second planned operation"));
    }

    [Fact]
    public async Task StopsApplyingAdditionalActionsAfterFindingIsVerified()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var finding = Finding("same-check");
        var first = new StubFixAction("first", RiskTier.Safe, events);
        var second = new StubFixAction("second", RiskTier.Safe, events);

        var results = await new FixEngine(manager).RunAsync(
            [(finding, first), (finding, second)],
            Context(manager, dryRun: false),
            null,
            CancellationToken.None);

        Assert.Equal(["first:backup", "first:apply", "first:verify"], events);
        Assert.Single(results);
        Assert.Equal(FindingStatus.Resolved, finding.Status);
    }

    [Fact]
    public async Task AggressiveFixRequiresConsentAndRestorePoint()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction("aggressive", RiskTier.Aggressive, events);
        var context = Context(manager, dryRun: false);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], context, null, CancellationToken.None);

        Assert.Empty(events);
        Assert.Equal("FixResult_ForceRequirementsMissing", result.Single().MessageKey);
    }

    [Fact]
    public async Task AggressiveFixCannotBypassAFailedBackup()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction("aggressive", RiskTier.Aggressive, events, backupSucceeds: false);
        var context = Context(
            manager,
            dryRun: false,
            forceModeConfirmed: true,
            restorePointAvailable: true);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], context, null, CancellationToken.None);

        Assert.Equal(["aggressive:backup"], events);
        Assert.False(result.Single().Applied);
        Assert.Equal("FixResult_BackupFailed", result.Single().MessageKey);
    }

    [Fact]
    public async Task AggressiveFixCanContinueWithoutBackupWhenExplicitlyAllowed()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction("aggressive", RiskTier.Aggressive, events, backupSucceeds: false);
        var context = Context(
            manager,
            dryRun: false,
            forceModeConfirmed: true,
            restorePointAvailable: true,
            allowBackuplessAggressive: true);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], context, null, CancellationToken.None);

        Assert.Equal(["aggressive:backup", "aggressive:apply", "aggressive:verify"], events);
        Assert.True(result.Single().Applied);
        Assert.True(result.Single().Verified);
        Assert.Null(result.Single().Backup);
        Assert.True(context.Session.Actions.Single().BackupOptional);
    }

    [Fact]
    public async Task ApplyFailureAutomaticallyRestoresBackup()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var backups = new StubBackupService { RestoreResult = true };
        var fix = new StubFixAction("safe", RiskTier.Safe, events, applySucceeds: false);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], Context(manager, dryRun: false, backups), null, CancellationToken.None);

        Assert.Equal(1, backups.RestoreCount);
        Assert.False(result.Single().Applied);
        Assert.Equal("FixResult_ApplyFailedRolledBack", result.Single().MessageKey);
    }

    [Fact]
    public async Task VerificationFailureAutomaticallyRestoresBackup()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var backups = new StubBackupService { RestoreResult = true };
        var fix = new StubFixAction("safe", RiskTier.Safe, events, verifySucceeds: false);

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)], Context(manager, dryRun: false, backups), null, CancellationToken.None);

        Assert.Equal(["safe:backup", "safe:apply", "safe:verify"], events);
        Assert.Equal(1, backups.RestoreCount);
        Assert.False(result.Single().Applied);
        Assert.Equal("FixResult_VerificationFailedRolledBack", result.Single().MessageKey);
    }

    [Fact]
    public async Task RecoveryIntentIsPersistedBeforeVerificationCompletes()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var fix = new StubFixAction(
            "durable",
            RiskTier.Safe,
            events,
            beforeApply: () => Assert.Contains(manager.SavedActionStates, snapshot =>
                snapshot.Count == 1 &&
                snapshot[0].Applied &&
                !snapshot[0].Verified &&
                snapshot[0].Result == "FixResult_ApplyInProgress"));

        var result = await new FixEngine(manager).RunAsync(
            [(Finding("check"), fix)],
            Context(manager, dryRun: false),
            null,
            CancellationToken.None);

        Assert.True(result.Single().Verified);
        Assert.Equal(["durable:backup", "durable:apply", "durable:verify"], events);
    }

    [Fact]
    public async Task ExactDiagnosticRecheckCanRejectAWeakActionVerifier()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var backups = new StubBackupService { RestoreResult = true };
        var finding = Finding("exact-check");
        var fix = new StubFixAction("safe", RiskTier.Safe, events, verifySucceeds: true);

        var result = await new FixEngine(manager).RunAsync(
            [new FixRequest(finding, fix, null, new StubDiagnosticCheck("exact-check", returnsFinding: true))],
            Context(manager, dryRun: false, backups),
            null,
            CancellationToken.None);

        Assert.Equal(1, backups.RestoreCount);
        Assert.Equal(FindingStatus.Unresolved, finding.Status);
        Assert.Equal("FixResult_VerificationFailedRolledBack", result.Single().MessageKey);
    }

    [Fact]
    public async Task EachTargetedRequestReceivesOnlyItsOwnParameters()
    {
        var manager = new RecordingSessionManager();
        var observedTargets = new List<string>();
        var fix = new ParameterRecordingFixAction(observedTargets);
        var context = Context(
            manager,
            dryRun: false,
            parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["targeted.target"] = "global-value-must-not-leak"
            });

        var results = await new FixEngine(manager).RunAsync(
            [
                new FixRequest(
                    Finding("first-check"),
                    fix,
                    new Dictionary<string, string> { ["targeted.target"] = "first-device" }),
                new FixRequest(
                    Finding("second-check"),
                    fix,
                    new Dictionary<string, string> { ["targeted.target"] = "second-device" })
            ],
            context,
            null,
            CancellationToken.None);

        Assert.Equal(["first-device", "second-device"], observedTargets);
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Verified));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingOrBlankTargetIsRejectedBeforeBackup(string? target)
    {
        var manager = new RecordingSessionManager();
        var observedTargets = new List<string>();
        var fix = new ParameterRecordingFixAction(observedTargets);
        IReadOnlyDictionary<string, string> parameters = target is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["targeted.target"] = target };

        var result = await new FixEngine(manager).RunAsync(
            [new FixRequest(Finding("target-check"), fix, parameters)],
            Context(manager, dryRun: false),
            null,
            CancellationToken.None);

        Assert.Empty(observedTargets);
        Assert.False(result.Single().Applied);
        Assert.Equal("FixResult_TargetRequired", result.Single().MessageKey);
    }

    [Fact]
    public async Task CancellationDuringApplyRollsBackAndPersistsCancelledSession()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var backups = new StubBackupService { RestoreResult = true };
        var fix = new StubFixAction("safe", RiskTier.Safe, events, cancelOnApply: true);
        var context = Context(manager, dryRun: false, backups);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FixEngine(manager).RunAsync(
                [(Finding("check"), fix)], context, null, CancellationToken.None));

        Assert.Equal(1, backups.RestoreCount);
        Assert.Equal(SessionStatus.Cancelled, context.Session.Status);
        Assert.False(context.Session.Actions.Single().Applied);
        Assert.Equal("FixResult_CancelledRolledBack", context.Session.Actions.Single().ResultMessageKey);
    }

    [Fact]
    public async Task CancellationDuringBackupLeavesNoRunningOrRepairingState()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var finding = Finding("check");
        var fix = new StubFixAction("safe", RiskTier.Safe, events, cancelOnBackup: true);
        var context = Context(manager, dryRun: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FixEngine(manager).RunAsync(
                [(finding, fix)], context, null, CancellationToken.None));

        Assert.Equal(["safe:backup"], events);
        Assert.Equal(SessionStatus.Cancelled, context.Session.Status);
        Assert.Equal(FindingStatus.Unresolved, finding.Status);
        Assert.True(manager.SaveCount >= 2);
    }

    [Fact]
    public async Task CancellationAfterARebootRepairPreservesPendingVerification()
    {
        var events = new List<string>();
        var manager = new RecordingSessionManager();
        var firstFinding = Finding("first");
        var secondFinding = Finding("second");
        var first = new StubFixAction("first-fix", RiskTier.Safe, events, requiresReboot: true);
        var second = new StubFixAction("second-fix", RiskTier.Safe, events, requiresReboot: true, cancelOnBackup: true);
        var context = Context(manager, dryRun: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FixEngine(manager).RunAsync(
                [(firstFinding, first), (secondFinding, second)], context, null, CancellationToken.None));

        Assert.True(context.Session.PendingVerify);
        Assert.Equal(SessionStatus.PendingVerification, context.Session.Status);
        Assert.Equal(FindingStatus.Repairing, firstFinding.Status);
        Assert.Equal(FindingStatus.Unresolved, secondFinding.Status);
        Assert.Null(context.Session.CompletedAt);
    }

    private static Finding Finding(string id) => new()
    {
        CheckId = id,
        ModuleId = "test",
        Severity = Severity.Warning,
        MessageKey = "test"
    };

    private static FixContext Context(
        RecordingSessionManager manager,
        bool dryRun,
        StubBackupService? backups = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        bool forceModeConfirmed = false,
        bool restorePointAvailable = false,
        bool allowBackuplessAggressive = false,
        EventConsoleSink? console = null) => new()
    {
        Commands = new RecordingCommandRunner(),
        Backups = backups ?? new StubBackupService(),
        Console = console ?? new EventConsoleSink(),
        Text = new PassthroughTextProvider(),
        Session = RecordingSessionManager.NewSession(dryRun),
        DryRun = dryRun,
        ForceModeConfirmed = forceModeConfirmed,
        RestorePointAvailable = restorePointAvailable,
        AllowBackuplessAggressive = allowBackuplessAggressive,
        Parameters = parameters ?? new Dictionary<string, string>()
    };

    private sealed class ParameterRecordingFixAction(List<string> observedTargets) : FixAction
    {
        public override string Id => "targeted";
        public override string TitleKey => Id;
        public override string ModuleId => "test";
        public override RiskTier Tier => RiskTier.Safe;
        public override bool RequiresTargetParameter => true;

        public override Task<BackupEntry?> BackupAsync(FixContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<BackupEntry?>(new BackupEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = BackupKind.Value,
                Label = Id,
                Location = Path.Combine(context.Session.DirectoryPath, Guid.NewGuid().ToString("N")),
                ContentHash = new string('0', 64)
            });
        }

        public override Task<FixResult> ApplyAsync(FixContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            observedTargets.Add(context.Parameters["targeted.target"]);
            return Task.FromResult(FixResult.Ok("ok"));
        }

        public override Task<bool> VerifyAsync(FixContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
