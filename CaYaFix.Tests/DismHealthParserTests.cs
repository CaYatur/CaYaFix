// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Modules.Shared;

namespace CaYaFix.Tests;

public sealed class DismHealthParserTests
{
    [Fact]
    public void ScanHealthCleanSuccessIsHealthy_NotFalseCorruption()
    {
        // Real-world false positive: output contains the word "corruption" inside the healthy phrase.
        const string output =
            """
            Deployment Image Servicing and Management tool
            Version: 10.0.28000.2307

            Image Version: 10.0.28020.2380

            [==========================100.0%==========================]
            No component store corruption detected.
            The operation completed successfully.
            """;

        var health = DismHealthParser.ParseHealthScan(output, string.Empty, exitCode: 0);
        Assert.Equal(DismHealthParser.ComponentStoreHealth.Healthy, health);
        Assert.False(DismHealthParser.NeedsCorruptionRepair(health));
    }

    [Fact]
    public void ProgressBarsAloneDoNotIndicateCorruption()
    {
        const string output =
            """
            =96.7%
            =100.0%
            No component store corruption detected.
            The operation completed successfully.
            """;

        Assert.Equal(
            DismHealthParser.ComponentStoreHealth.Healthy,
            DismHealthParser.ParseHealthScan(output, string.Empty, 0));
    }

    [Fact]
    public void RepairablePhraseIsDetected()
    {
        const string output =
            """
            The component store is repairable.
            The operation completed successfully.
            """;

        var health = DismHealthParser.ParseHealthScan(output, string.Empty, 0);
        Assert.Equal(DismHealthParser.ComponentStoreHealth.Repairable, health);
        Assert.True(DismHealthParser.NeedsCorruptionRepair(health));
    }

    [Fact]
    public void NotRepairableIsNotClassifiedAsRepairable()
    {
        const string output =
            """
            The component store is not repairable.
            The operation completed successfully.
            """;

        Assert.Equal(
            DismHealthParser.ComponentStoreHealth.NotRepairable,
            DismHealthParser.ParseHealthScan(output, string.Empty, 0));
    }

    [Fact]
    public void Error14098IsNotRepairable()
    {
        const string output =
            """
            Error: 14098
            The component store has been corrupted.
            """;

        Assert.Equal(
            DismHealthParser.ComponentStoreHealth.NotRepairable,
            DismHealthParser.ParseHealthScan(output, string.Empty, 14098));
    }

    [Fact]
    public void AnalyzeCleanupRecommendedYesIsTrue()
    {
        const string output =
            """
            Component Store (WinSxS) information:

            Windows Explorer Reported Size of Component Store : 20.63 GB
            Actual Size of Component Store : 18.64 GB
            Shared with Windows : 8.06 GB
            Backups and Disabled Features : 10.57 GB
            Cache and Temporary Data : 0 bytes

            Date of Last Cleanup : 2026-07-16 01:36:46

            Number of Reclaimable Packages : 5
            Component Store Cleanup Recommended : Yes

            The operation completed successfully.
            """;

        Assert.True(DismHealthParser.IsCleanupRecommended(output, string.Empty));
        Assert.Equal(5, DismHealthParser.TryParseReclaimablePackages(output, string.Empty));
    }

    [Fact]
    public void AnalyzeCleanupRecommendedNoIsFalse_EvenWithReclaimableWord()
    {
        const string output =
            """
            Number of Reclaimable Packages : 0
            Component Store Cleanup Recommended : No
            The operation completed successfully.
            """;

        Assert.False(DismHealthParser.IsCleanupRecommended(output, string.Empty));
        Assert.Equal(0, DismHealthParser.TryParseReclaimablePackages(output, string.Empty));
    }

    [Fact]
    public void BareReclaimableWordIsNotCleanupRecommendation()
    {
        // Old buggy matcher used Contains("reclaimable") which always fired.
        const string output =
            """
            Number of Reclaimable Packages : 0
            Component Store Cleanup Recommended : No
            """;

        Assert.False(DismHealthParser.IsCleanupRecommended(output, string.Empty));
    }

    [Fact]
    public void NonZeroExitWithoutPhraseIsUnknownFailure()
    {
        Assert.Equal(
            DismHealthParser.ComponentStoreHealth.UnknownFailure,
            DismHealthParser.ParseHealthScan("Unexpected failure.", string.Empty, exitCode: 2));
    }

    [Fact]
    public void HealthyPhraseWinsOverNonZeroExitWhenPresent()
    {
        // Prefer explicit DISM English result over a confusing exit code.
        const string output = "No component store corruption detected.\r\nThe operation completed successfully.";
        Assert.Equal(
            DismHealthParser.ComponentStoreHealth.Healthy,
            DismHealthParser.ParseHealthScan(output, string.Empty, exitCode: 1));
    }
}
