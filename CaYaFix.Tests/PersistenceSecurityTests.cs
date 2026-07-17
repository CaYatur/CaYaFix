// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;

namespace CaYaFix.Tests;

public sealed class PersistenceSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CaYaFix.Tests", Guid.NewGuid().ToString("N"));
    private readonly IIntegrityService _integrity = new EphemeralIntegrityService(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task TamperedManifestIsRejected()
    {
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        var manifest = Path.Combine(session.DirectoryPath, "manifest.json");
        var content = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(manifest, content.Replace("\"DryRun\": false", "\"DryRun\": true", StringComparison.Ordinal));

        var sessions = await manager.ListAsync(CancellationToken.None);

        Assert.Empty(sessions);
    }

    [Fact]
    public async Task AtomicManifestEnvelopeRemainsReadableAcrossRepeatedSaves()
    {
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        session.Status = SessionStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;

        await manager.SaveAsync(session, CancellationToken.None);
        var loaded = Assert.Single(await manager.ListAsync(CancellationToken.None));

        Assert.Equal(SessionStatus.Completed, loaded.Status);
        Assert.NotNull(loaded.CompletedAt);
        Assert.False(File.Exists(Path.Combine(session.DirectoryPath, "manifest.sig")));
    }

    [Fact]
    public async Task InterruptedWriteAheadActionIsReturnedBeforeRebootVerificationSessions()
    {
        var manager = new SessionManager(_root, _integrity);
        var interrupted = await manager.CreateAsync(false, CancellationToken.None);
        interrupted.Actions.Add(new SessionActionRecord
        {
            FixId = "test.interrupted",
            FindingCheckId = "test.check",
            Tier = RiskTier.Safe,
            Applied = true,
            Verified = false,
            ResultMessageKey = "FixResult_ApplyInProgress",
            Backup = new BackupEntry
            {
                Id = "backup",
                Kind = BackupKind.Value,
                Label = "test",
                Location = Path.Combine(interrupted.DirectoryPath, "Backups", "test.json"),
                ContentHash = new string('0', 64)
            }
        });
        await manager.SaveAsync(interrupted, CancellationToken.None);

        await Task.Delay(1);
        var reboot = await manager.CreateAsync(false, CancellationToken.None);
        reboot.PendingVerify = true;
        reboot.RequiresReboot = true;
        reboot.Status = SessionStatus.PendingVerification;
        await manager.SaveAsync(reboot, CancellationToken.None);

        var pending = await manager.GetPendingAsync(CancellationToken.None);

        Assert.NotNull(pending);
        Assert.Equal(interrupted.Id, pending.Id);
    }

    [Theory]
    [InlineData("FixResult_ApplyFailedRollbackFailed")]
    [InlineData("FixResult_VerificationFailedRollbackFailed")]
    [InlineData("FixResult_CancelledRollbackFailed")]
    public async Task FailedAutomaticRollbackSessionsAreSurfacedAsPendingRecovery(string resultKey)
    {
        var manager = new SessionManager(_root, _integrity);
        var failedRollback = await manager.CreateAsync(false, CancellationToken.None);
        failedRollback.Status = SessionStatus.Failed;
        failedRollback.Actions.Add(new SessionActionRecord
        {
            FixId = "test.failed-rollback",
            FindingCheckId = "test.check",
            Tier = RiskTier.Safe,
            Applied = true,
            Verified = false,
            Undone = false,
            ResultMessageKey = resultKey,
            Backup = new BackupEntry
            {
                Id = "backup",
                Kind = BackupKind.Value,
                Label = "test",
                Location = Path.Combine(failedRollback.DirectoryPath, "Backups", "test.json"),
                ContentHash = new string('a', 64)
            }
        });
        await manager.SaveAsync(failedRollback, CancellationToken.None);

        await Task.Delay(1);
        var reboot = await manager.CreateAsync(false, CancellationToken.None);
        reboot.PendingVerify = true;
        reboot.RequiresReboot = true;
        reboot.Status = SessionStatus.PendingVerification;
        await manager.SaveAsync(reboot, CancellationToken.None);

        var pending = await manager.GetPendingAsync(CancellationToken.None);

        Assert.NotNull(pending);
        Assert.Equal(failedRollback.Id, pending.Id);
        Assert.True(SessionRecoveryGates.SessionRequiresRecovery(pending));
    }

    [Fact]
    public void SessionRecoveryGatesIgnoreSuccessfulOrRebootPendingActions()
    {
        Assert.False(SessionRecoveryGates.RequiresRecovery(new SessionActionRecord
        {
            FixId = "ok",
            FindingCheckId = "check",
            Applied = true,
            Verified = true,
            ResultMessageKey = "FixResult_Verified",
            Backup = DummyBackup()
        }));
        Assert.False(SessionRecoveryGates.RequiresRecovery(new SessionActionRecord
        {
            FixId = "reboot",
            FindingCheckId = "check",
            Applied = true,
            Verified = false,
            RequiresReboot = true,
            ResultMessageKey = "FixResult_PendingReboot",
            Backup = DummyBackup()
        }));
        Assert.True(SessionRecoveryGates.RequiresRecovery(new SessionActionRecord
        {
            FixId = "stuck",
            FindingCheckId = "check",
            Applied = true,
            Verified = false,
            ResultMessageKey = "FixResult_ApplyInProgress",
            Backup = DummyBackup()
        }));
    }

    private static BackupEntry DummyBackup() => new()
    {
        Id = "backup",
        Kind = BackupKind.Value,
        Label = "test",
        Location = @"C:\temp\backup.json",
        ContentHash = new string('b', 64)
    };

    [Fact]
    public async Task SessionHistoryKeepsOnlyTheNewestBoundedSet()
    {
        var manager = new SessionManager(_root, _integrity);
        var sessionsRoot = Path.Combine(_root, "Sessions");
        var oldest = new SessionManifest
        {
            Id = "20000101-000000-000000",
            DirectoryPath = Path.Combine(sessionsRoot, "20000101-000000-000000"),
            DryRun = false
        };
        var newest = new SessionManifest
        {
            Id = "20990101-000000-000000",
            DirectoryPath = Path.Combine(sessionsRoot, "20990101-000000-000000"),
            DryRun = false
        };
        await manager.SaveAsync(oldest, CancellationToken.None);
        await manager.SaveAsync(newest, CancellationToken.None);

        for (var index = 0; index < 520; index++)
        {
            Directory.CreateDirectory(Path.Combine(sessionsRoot, $"20500101-000000-{index:X6}"));
        }

        Directory.CreateDirectory(Path.Combine(sessionsRoot, "untrusted-name"));
        var loaded = await manager.ListAsync(CancellationToken.None);

        Assert.Equal("20990101-000000-000000", Assert.Single(loaded).Id);
        Assert.DoesNotContain(loaded, session => session.Id == oldest.Id);
    }

    [Fact]
    public async Task SessionCannotBeSavedOutsideTrustedRoot()
    {
        var manager = new SessionManager(_root, _integrity);
        var escaped = new SessionManifest
        {
            Id = "escape",
            DirectoryPath = Path.Combine(_root, "escape"),
            DryRun = false
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(escaped, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedManifestRecordCollectionIsRejectedWithoutReplacingTheTrustedManifest()
    {
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        for (var index = 0; index < 4_097; index++)
        {
            session.Findings.Add(new SessionFindingRecord
            {
                CheckId = $"check-{index}",
                ModuleId = "test",
                Severity = Severity.Info,
                MessageKey = "Finding_Network_NoAdapters",
                Status = FindingStatus.Open
            });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(session, CancellationToken.None));
        var loaded = Assert.Single(await manager.ListAsync(CancellationToken.None));
        Assert.Empty(loaded.Findings);
    }

    [Fact]
    public async Task InconsistentAppliedActionWithoutBackupIsRejected()
    {
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        session.Actions.Add(new SessionActionRecord
        {
            FixId = "test.fix",
            FindingCheckId = "test.check",
            Tier = RiskTier.Safe,
            Applied = true,
            ResultMessageKey = "FixResult_Verified"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task SupportPackageRejectsAnUntrustedSessionBeforeRunningCommands()
    {
        var runner = new RecordingCommandRunner();
        var service = new SupportPackageService(runner, _root);
        var outside = Path.Combine(_root, "Outside", "untrusted");
        Directory.CreateDirectory(outside);
        var session = new SessionManifest
        {
            Id = "untrusted",
            DirectoryPath = outside,
            DryRun = false
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(session, null, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task BackupCannotEscapeSessionRoot()
    {
        var backups = new BackupService(new RecordingCommandRunner(), _root);
        var outside = Path.Combine(_root, "Outside");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backups.CaptureValueAsync("escape", new { Value = 1 }, outside, CancellationToken.None));
    }

    [Fact]
    public async Task TamperedBackupIsRejectedBeforeRestoreCommand()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var directory = BackupDirectory("tamper");
        var entry = await backups.CaptureCommandStateAsync(
            "state", "cmd.exe", ["/d", "/c", "capture"],
            "cmd.exe", ["/d", "/c", "restore"], directory, CancellationToken.None);
        Assert.NotNull(entry);
        await File.AppendAllTextAsync(entry.Location, "tampered");

        var restored = await backups.RestoreAsync(entry, CancellationToken.None);

        Assert.False(restored);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task InvalidDriverIdentifierIsRejectedWithoutCommand()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);

        var entry = await backups.CaptureDriverAsync("..\\malicious.inf", BackupDirectory("driver"), CancellationToken.None);

        Assert.Null(entry);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RegisteredRestoreHandlerRunsOnlyForUntamperedBackup()
    {
        var calls = 0;
        var backups = new BackupService(new RecordingCommandRunner(), _root);
        backups.RegisterRestoreHandler("test-handler-v1", (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        });
        var entry = await backups.CaptureValueAsync(
            "custom-state",
            new { Value = 1 },
            BackupDirectory("custom-handler"),
            CancellationToken.None);
        Assert.NotNull(entry);
        entry.Metadata["restoreHandler"] = "test-handler-v1";

        Assert.True(await backups.RestoreAsync(entry, CancellationToken.None));
        Assert.Equal(1, calls);

        await File.AppendAllTextAsync(entry.Location, "tampered");
        Assert.False(await backups.RestoreAsync(entry, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BundleEntryCountIsBoundedBeforeTheBundleFileIsCreated()
    {
        var backups = new BackupService(new RecordingCommandRunner(), _root);
        var directory = BackupDirectory("bundle-bound");
        var child = await backups.CaptureValueAsync("child", new { Value = 1 }, directory, CancellationToken.None);
        Assert.NotNull(child);

        var bundle = await backups.CaptureBundleAsync(
            "oversized-bundle",
            Enumerable.Repeat(child, 129).ToArray(),
            directory,
            CancellationToken.None);

        Assert.Null(bundle);
        Assert.Empty(Directory.EnumerateFiles(directory, "bundle-*.json"));
    }

    [Fact]
    public async Task OversizedRestoreMetadataIsRejectedBeforeRunningACommand()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var entry = await backups.CaptureValueAsync(
            "metadata-bound",
            new { Value = 1 },
            BackupDirectory("metadata-bound"),
            CancellationToken.None);
        Assert.NotNull(entry);
        entry.Metadata["restoreExecutable"] = "cmd.exe";
        entry.Metadata["restoreArguments"] = new string('x', 256 * 1024 + 1);

        Assert.False(await backups.RestoreAsync(entry, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PowerPlanValueBackupCanUseTheAllowlistedPowercfgRestoreCommand()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var entry = await backups.CaptureValueAsync(
            "power-plan",
            new { Guid = "381b4222-f694-41f0-9685-ff5bb260df2e" },
            BackupDirectory("power-plan"),
            CancellationToken.None);
        Assert.NotNull(entry);
        entry.Metadata["restoreExecutable"] = "powercfg.exe";
        entry.Metadata["restoreArguments"] = "[\"/setactive\",\"381b4222-f694-41f0-9685-ff5bb260df2e\"]";

        Assert.True(await backups.RestoreAsync(entry, CancellationToken.None));
        var call = Assert.Single(runner.Calls);
        Assert.Equal("powercfg.exe", call.Executable);
        Assert.Equal("/setactive", call.Arguments[0]);
    }

    [Fact]
    public async Task RollbackRunsInReverseOrder()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        for (var index = 1; index <= 3; index++)
        {
            var entry = await backups.CaptureCommandStateAsync(
                $"state-{index}", "cmd.exe", ["/d", "/c", $"capture-{index}"],
                "cmd.exe", ["/d", "/c", $"restore-{index}"], Path.Combine(session.DirectoryPath, "Backups"), CancellationToken.None);
            Assert.NotNull(entry);
            session.Actions.Add(new SessionActionRecord
            {
                FixId = $"fix-{index}",
                FindingCheckId = $"check-{index}",
                Tier = RiskTier.Safe,
                Applied = true,
                Backup = entry
            });
        }
        await manager.SaveAsync(session, CancellationToken.None);

        var restored = await manager.UndoSessionAsync(session, backups, CancellationToken.None);
        var restoreCalls = runner.Calls.Skip(3).Select(call => call.Arguments.Last()).ToArray();

        Assert.True(restored);
        Assert.Equal(["restore-3", "restore-2", "restore-1"], restoreCalls);
    }

    [Fact]
    public async Task SingleActionRollbackRestoresOnlyTheSelectedBackup()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        for (var index = 1; index <= 2; index++)
        {
            var entry = await backups.CaptureCommandStateAsync(
                $"state-{index}", "cmd.exe", ["/d", "/c", $"capture-{index}"],
                "cmd.exe", ["/d", "/c", $"restore-{index}"], Path.Combine(session.DirectoryPath, "Backups"), CancellationToken.None);
            Assert.NotNull(entry);
            session.Actions.Add(new SessionActionRecord
            {
                FixId = $"fix-{index}",
                FindingCheckId = $"check-{index}",
                Tier = RiskTier.Safe,
                Applied = true,
                Backup = entry
            });
        }
        await manager.SaveAsync(session, CancellationToken.None);

        var restored = await manager.UndoActionAsync(session, 0, backups, CancellationToken.None);
        var loaded = Assert.Single(await manager.ListAsync(CancellationToken.None));

        Assert.True(restored);
        Assert.Equal("restore-1", runner.Calls.Last().Arguments.Last());
        Assert.True(loaded.Actions[0].Undone);
        Assert.False(loaded.Actions[1].Undone);
    }

    [Fact]
    public async Task UndoingTheOnlyPendingActionClearsRebootAndVerificationFlags()
    {
        var runner = new RecordingCommandRunner();
        var backups = new BackupService(runner, _root);
        var manager = new SessionManager(_root, _integrity);
        var session = await manager.CreateAsync(false, CancellationToken.None);
        var entry = await backups.CaptureCommandStateAsync(
            "pending", "cmd.exe", ["/d", "/c", "capture"],
            "cmd.exe", ["/d", "/c", "restore"], Path.Combine(session.DirectoryPath, "Backups"), CancellationToken.None);
        Assert.NotNull(entry);
        session.Actions.Add(new SessionActionRecord
        {
            FixId = "reboot-fix",
            FindingCheckId = "reboot-check",
            Tier = RiskTier.Moderate,
            Applied = true,
            RequiresReboot = true,
            Backup = entry
        });
        session.PendingVerify = true;
        session.RequiresReboot = true;
        session.Status = SessionStatus.PendingVerification;
        await manager.SaveAsync(session, CancellationToken.None);

        Assert.True(await manager.UndoActionAsync(session, 0, backups, CancellationToken.None));
        var loaded = Assert.Single(await manager.ListAsync(CancellationToken.None));
        Assert.False(loaded.PendingVerify);
        Assert.False(loaded.RequiresReboot);
        Assert.Equal(SessionStatus.RolledBack, loaded.Status);
    }

    private string BackupDirectory(string session)
    {
        var path = Path.Combine(_root, "Sessions", session, "Backups");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
