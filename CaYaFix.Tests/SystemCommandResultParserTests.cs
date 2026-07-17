// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using CaYaFix.Modules.Shared;

namespace CaYaFix.Tests;

public sealed class SystemCommandResultParserTests
{
    [Fact]
    public void ChkdskExit0WithoutProblemTextIsClean()
    {
        const string output =
            """
            The type of the file system is NTFS.
            Stage 1: Examining basic file system structure ...
            100 percent complete.
            """;
        Assert.True(SystemCommandResultParsers.IsChkdskOnlineScanClean(output, string.Empty, 0));
    }

    [Fact]
    public void ChkdskHealthyBannerIsClean()
    {
        const string output = "Windows has scanned the file system and found no problems.\r\nNo further action is required.";
        Assert.True(SystemCommandResultParsers.IsChkdskOnlineScanClean(output, string.Empty, 0));
    }

    [Fact]
    public void ChkdskProblemsFoundIsNotClean()
    {
        const string output = "Windows found problems with the file system.\r\nRun CHKDSK offline.";
        Assert.False(SystemCommandResultParsers.IsChkdskOnlineScanClean(output, string.Empty, 1));
    }

    [Fact]
    public void ChkdskBroadTurkishBulunamadiAloneIsNotEnoughWhenExitNonZero()
    {
        // Old matcher treated any "bulunamadı" as clean — too broad.
        const string output = "Belirtilen dosya bulunamadı.";
        Assert.False(SystemCommandResultParsers.IsChkdskOnlineScanClean(output, string.Empty, 3));
    }

    [Fact]
    public void WinReStatusParsesEnabledLineOnly()
    {
        const string output =
            """
            Windows Recovery Environment (Windows RE) and system reset configuration
            Information:

            Windows RE status:         Enabled
            Windows RE location:       \\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE
            Boot Configuration Data (BCD) identifier: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
            """;
        Assert.Equal(
            SystemCommandResultParsers.WinReStatus.Enabled,
            SystemCommandResultParsers.ParseWinReStatus(output, string.Empty));
    }

    [Fact]
    public void WinReStatusParsesDisabledLine()
    {
        const string output = "Windows RE status:         Disabled\r\nWindows RE location:";
        Assert.Equal(
            SystemCommandResultParsers.WinReStatus.Disabled,
            SystemCommandResultParsers.ParseWinReStatus(output, string.Empty));
    }

    [Fact]
    public void WinReDoesNotTreatRandomEnabledTokenAsStatus()
    {
        const string output = "Some other feature is Enabled\r\nNo Windows RE status line here";
        Assert.Equal(
            SystemCommandResultParsers.WinReStatus.Unknown,
            SystemCommandResultParsers.ParseWinReStatus(output, string.Empty));
    }

    [Fact]
    public void SfcCleanPhrase()
    {
        Assert.Equal(
            SystemCommandResultParsers.SfcOutcome.Clean,
            SystemCommandResultParsers.ParseSfc(
                "Windows Resource Protection did not find any integrity violations.",
                string.Empty,
                0));
    }

    [Fact]
    public void SfcCorruptUnfixed()
    {
        Assert.Equal(
            SystemCommandResultParsers.SfcOutcome.CorruptUnfixed,
            SystemCommandResultParsers.ParseSfc(
                "Windows Resource Protection found corrupt files but was unable to fix some of them.",
                string.Empty,
                1));
    }

    [Fact]
    public void TcpAutotuningRequiresNormalLevelNotBareEnabled()
    {
        const string output =
            """
            TCP Global Parameters
            ----------------------------------------------
            Receive-Side Scaling State          : enabled
            Receive Window Auto-Tuning Level    : normal
            Add-On Congestion Control Provider  : default
            """;
        Assert.True(SystemCommandResultParsers.IsTcpAutotuningNormal(output, string.Empty));

        const string onlyEnabled =
            """
            Receive-Side Scaling State          : enabled
            ECN Capability                      : enabled
            """;
        Assert.False(SystemCommandResultParsers.IsTcpAutotuningNormal(onlyEnabled, string.Empty));
    }

    [Fact]
    public void RecoveryEnabledYesIsDetected()
    {
        const string output =
            """
            Windows Boot Loader
            -------------------
            identifier              {current}
            device                  partition=C:
            path                    \Windows\system32\winload.efi
            recoveryenabled         Yes
            """;
        Assert.True(SystemCommandResultParsers.IsRecoveryEnabledYes(output, string.Empty));
        Assert.False(SystemCommandResultParsers.IsRecoveryEnabledYes(
            "identifier {current}\r\nrecoveryenabled         No",
            string.Empty));
    }

    [Fact]
    public void WmiRepositoryInconsistentWinsOverConsistentSubstring()
    {
        // "consistent" is a substring of "INCONSISTENT"; the parser must not misread it.
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Inconsistent,
            SystemCommandResultParsers.ParseWmiRepositoryState(
                "WMI repository is INCONSISTENT", string.Empty, 0));
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Consistent,
            SystemCommandResultParsers.ParseWmiRepositoryState(
                "WMI repository is consistent", string.Empty, 0));
    }

    [Fact]
    public void WmiRepositoryTurkishPhrasesAreRecognized()
    {
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Inconsistent,
            SystemCommandResultParsers.ParseWmiRepositoryState(
                "WMI deposu tutarsız", string.Empty, 0));
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Consistent,
            SystemCommandResultParsers.ParseWmiRepositoryState(
                "WMI deposu tutarlı", string.Empty, 0));
    }

    [Fact]
    public void WmiRepositoryFallsBackToExitCodeWithoutPhrases()
    {
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Consistent,
            SystemCommandResultParsers.ParseWmiRepositoryState(string.Empty, string.Empty, 0));
        Assert.Equal(
            SystemCommandResultParsers.WmiRepositoryState.Unknown,
            SystemCommandResultParsers.ParseWmiRepositoryState(string.Empty, "error", 2));
    }
}
