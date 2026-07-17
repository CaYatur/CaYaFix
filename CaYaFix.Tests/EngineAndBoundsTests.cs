// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Core;

namespace CaYaFix.Tests;

public sealed class EngineAndBoundsTests
{
    [Fact]
    public void EventConsoleKeepsOnlyItsBoundedTail()
    {
        var sink = new EventConsoleSink(10);
        for (var index = 0; index < 100; index++)
        {
            sink.Write(new ConsoleLine(DateTimeOffset.UtcNow, "OUT", index.ToString()));
        }

        var snapshot = sink.Snapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.Equal("90", snapshot[0].Text);
        Assert.Equal("99", snapshot[^1].Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventConsoleSink(0));
    }

    [Fact]
    public async Task DiagnosticParallelismNeverExceedsConfiguredBound()
    {
        var current = 0;
        var maximum = 0;
        var checks = Enumerable.Range(0, 18).Select(index =>
            new TestCheck($"check-{index}", async ct =>
            {
                var active = Interlocked.Increment(ref current);
                UpdateMaximum(ref maximum, active);
                try
                {
                    await Task.Delay(25, ct);
                    return null;
                }
                finally
                {
                    Interlocked.Decrement(ref current);
                }
            })).Cast<DiagnosticCheck>().ToArray();

        var results = await new DiagnosticEngine(3).RunAsync(
            [new TestModule(checks)],
            Context(),
            quickOnly: false,
            progress: null,
            CancellationToken.None);

        Assert.Empty(results);
        Assert.InRange(maximum, 1, 3);
    }

    [Fact]
    public async Task DiagnosticCancellationPropagatesPromptly()
    {
        var check = new TestCheck("wait", async ct =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return null;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DiagnosticEngine(1).RunAsync(
                [new TestModule([check])], Context(), false, null, cts.Token));
    }

    [Fact]
    public async Task FailedDiagnosticBecomesAnInformationalFinding()
    {
        var check = new TestCheck("broken", _ => throw new InvalidOperationException("simulated"));

        var result = await new DiagnosticEngine(1).RunAsync(
            [new TestModule([check])], Context(), false, null, CancellationToken.None);

        var finding = Assert.Single(result);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Finding_CheckCouldNotRun", finding.MessageKey);
        Assert.Contains("simulated", finding.TechnicalDetail);
    }

    [Fact]
    public async Task DiagnosticResultsAreNormalizedAndBoundedBeforeReachingTheUi()
    {
        var parameters = Enumerable.Range(0, 100)
            .ToDictionary(index => $"test.fix.{index}", index => new string('v', 32));
        var check = new TestCheck("bounded", _ => Task.FromResult<Finding?>(new Finding
        {
            CheckId = "wrong-check",
            ModuleId = "wrong-module",
            Severity = (Severity)999,
            MessageKey = new string('m', 300),
            MessageArguments = Enumerable.Repeat<object>(new string('a', 8_000), 100).ToArray(),
            TechnicalDetail = new string('x', 100_000) + "\0secret-tail",
            RecommendedFixIds = Enumerable.Range(0, 100).Select(index => $"test.fix.{index}").ToArray(),
            RepairParameters = parameters,
            Status = (FindingStatus)999
        }));

        var finding = Assert.Single(await new DiagnosticEngine(1).RunAsync(
            [new TestModule([check])], Context(), false, null, CancellationToken.None));

        Assert.Equal("bounded", finding.CheckId);
        Assert.Equal("test-module", finding.ModuleId);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(FindingStatus.Open, finding.Status);
        Assert.Equal("Finding_CheckCouldNotRun", finding.MessageKey);
        Assert.Equal(32, finding.MessageArguments.Length);
        Assert.All(finding.MessageArguments, argument => Assert.InRange(((string)argument).Length, 1, 4_096));
        Assert.InRange(finding.TechnicalDetail.Length, 1, 64 * 1024);
        Assert.DoesNotContain('\0', finding.TechnicalDetail);
        Assert.Equal(64, finding.RecommendedFixIds.Count);
        Assert.Equal(64, finding.RepairParameters.Count);
    }

    [Fact]
    public void TierPlannerSkipsUnavailableLevelsAndStopsAfterTheLastCandidate()
    {
        Assert.Equal(
            RiskTier.Aggressive,
            RepairTierPlanner.NextAvailableTier(
                RiskTier.Safe,
                [RiskTier.Safe, RiskTier.Aggressive, RiskTier.Aggressive]));
        Assert.Null(RepairTierPlanner.NextAvailableTier(
            RiskTier.Aggressive,
            [RiskTier.Safe, RiskTier.Moderate, RiskTier.Aggressive]));
        Assert.Null(RepairTierPlanner.NextAvailableTier(
            RiskTier.Moderate,
            [(RiskTier)999, RiskTier.Safe]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RepairTierPlanner.NextAvailableTier((RiskTier)0, [RiskTier.Safe]));
    }

    private static DiagnosticContext Context() => new()
    {
        Commands = new RecordingCommandRunner(),
        Text = new PassthroughTextProvider(),
        Thresholds = new Thresholds()
    };

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (observed >= value || Interlocked.CompareExchange(ref maximum, value, observed) == observed) return;
        }
    }

    private sealed class TestCheck : DiagnosticCheck
    {
        private readonly Func<CancellationToken, Task<Finding?>> _run;

        public TestCheck(string id, Func<CancellationToken, Task<Finding?>> run)
        {
            Id = id;
            _run = run;
        }

        public override string Id { get; }
        public override string TitleKey => Id;
        public override string ModuleId => "test-module";
        public override Task<Finding?> RunAsync(DiagnosticContext context, CancellationToken ct) => _run(ct);
    }

    private sealed class TestModule : IModuleDefinition
    {
        public TestModule(IReadOnlyList<DiagnosticCheck> checks) => Checks = checks;
        public ModuleInfo Info { get; } = new("test-module", "name", "description", "test.svg", 0);
        public IReadOnlyList<DiagnosticCheck> Checks { get; }
        public IReadOnlyList<FixAction> Fixes { get; } = [];
        public IReadOnlyList<LiveTest> LiveTests { get; } = [];
        public IReadOnlyList<Playbook> Playbooks { get; } = [];
    }
}
