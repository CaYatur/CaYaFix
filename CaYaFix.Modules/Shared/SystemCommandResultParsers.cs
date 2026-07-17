// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Modules.Shared;

/// <summary>
/// Result interpreters for Microsoft CLI tools. Prefer exit codes and documented
/// English phrases (plus common TR locales) over naive substring matches that
/// create false positives (e.g. matching "corruption" inside "no corruption").
/// </summary>
public static class SystemCommandResultParsers
{
    /// <summary>
    /// Interprets <c>chkdsk X: /scan</c> (online scan).
    /// Exit 0 without problem language is treated as clean.
    /// </summary>
    public static bool IsChkdskOnlineScanClean(string? stdout, string? stderr, int exitCode)
    {
        var text = Combine(stdout, stderr);
        if (HasChkdskProblemPhrase(text))
        {
            return false;
        }

        if (exitCode == 0)
        {
            return true;
        }

        // Some locales still print the healthy banner with a non-zero secondary code.
        return HasChkdskHealthyPhrase(text);
    }

    public static bool HasChkdskProblemPhrase(string text) =>
        Contains(text, "Windows found problems") ||
        Contains(text, "found problems that it cannot fix") ||
        Contains(text, "errors found") ||
        Contains(text, "failed to transfer logged messages") ||
        Contains(text, "Cannot open volume") ||
        Contains(text, "Access is denied") ||
        Contains(text, "The type of the file system is RAW") ||
        Contains(text, "sorun buldu") ||
        Contains(text, "hatalar bulundu");

    public static bool HasChkdskHealthyPhrase(string text) =>
        Contains(text, "found no problems") ||
        Contains(text, "no problems found") ||
        Contains(text, "Windows has scanned the file system and found no problems") ||
        Contains(text, "Windows has checked the file system and found no problems") ||
        Contains(text, "No further action is required") ||
        Contains(text, "sorun bulunamadı") ||
        Contains(text, "başka bir işlem gerekmiyor");

    /// <summary>
    /// Parses <c>reagentc /info</c> for the Windows RE status line.
    /// </summary>
    public static WinReStatus ParseWinReStatus(string? stdout, string? stderr)
    {
        var text = Combine(stdout, stderr);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!Contains(line, "Windows RE status") && !Contains(line, "Windows RE durumu"))
            {
                continue;
            }

            var colon = line.LastIndexOf(':');
            var value = colon >= 0 ? line[(colon + 1)..].Trim() : line;
            if (Contains(value, "Enabled") || Contains(value, "Etkin"))
            {
                return WinReStatus.Enabled;
            }

            if (Contains(value, "Disabled") || Contains(value, "Devre Dışı") || Contains(value, "Devre disi"))
            {
                return WinReStatus.Disabled;
            }
        }

        // Fallback: only when no status line was present.
        if (string.IsNullOrWhiteSpace(text))
        {
            return WinReStatus.Unknown;
        }

        // Avoid treating random "Enabled"/"Disabled" tokens elsewhere as status.
        return WinReStatus.Unknown;
    }

    public enum WinReStatus
    {
        Unknown,
        Enabled,
        Disabled
    }

    /// <summary>
    /// Interprets <c>sfc /scannow</c> or <c>sfc /verifyonly</c> messages.
    /// </summary>
    public static SfcOutcome ParseSfc(string? stdout, string? stderr, int exitCode)
    {
        var text = Combine(stdout, stderr);
        if (Contains(text, "did not find any integrity violations") ||
            Contains(text, "bütünlük ihlali bulamadı") ||
            Contains(text, "butunluk ihlali bulamadi"))
        {
            return SfcOutcome.Clean;
        }

        if (Contains(text, "found corrupt files and successfully repaired them") ||
            Contains(text, "bozuk dosyalar buldu ve bunları başarıyla onardı") ||
            Contains(text, "successfully repaired them"))
        {
            return SfcOutcome.Repaired;
        }

        if (Contains(text, "found corrupt files but was unable to fix some of them") ||
            Contains(text, "found integrity violations") ||
            Contains(text, "unable to fix some") ||
            Contains(text, "onaramadı") ||
            Contains(text, "bozuk dosyalar buldu"))
        {
            return SfcOutcome.CorruptUnfixed;
        }

        if (Contains(text, "There is a system repair pending") ||
            Contains(text, "bekleyen bir sistem onarımı") ||
            Contains(text, "pending which requires reboot"))
        {
            return SfcOutcome.PendingReboot;
        }

        // Exit 0/1 with empty or progress-only output is often still success after a long scan.
        return exitCode is 0 or 1 ? SfcOutcome.Clean : SfcOutcome.Failed;
    }

    public enum SfcOutcome
    {
        Clean,
        Repaired,
        CorruptUnfixed,
        PendingReboot,
        Failed
    }

    public static bool IsSfcAcceptable(SfcOutcome outcome) =>
        outcome is SfcOutcome.Clean or SfcOutcome.Repaired or SfcOutcome.PendingReboot;

    /// <summary>
    /// Verifies <c>netsh interface tcp show global</c> after normalize-tcp.
    /// </summary>
    public static bool IsTcpAutotuningNormal(string? stdout, string? stderr)
    {
        var text = Combine(stdout, stderr);
        // Match the documented key rather than bare "normal"/"enabled" anywhere.
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (Contains(line, "Receive Window Auto-Tuning Level") ||
                Contains(line, "Otomatik Ayarlama Düzeyi") ||
                Contains(line, "Auto-Tuning Level"))
            {
                return Contains(line, "normal") || Contains(line, "normal ");
            }
        }

        // Some builds print "Receive Window Auto-Tuning Level    : normal" without colon variants.
        return Contains(text, "Auto-Tuning Level") && Contains(text, "normal");
    }

    /// <summary>
    /// Verifies <c>bcdedit /enum {current}</c> recoveryenabled is Yes after repair.
    /// </summary>
    public static bool IsRecoveryEnabledYes(string? stdout, string? stderr)
    {
        var text = Combine(stdout, stderr);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!Contains(line, "recoveryenabled"))
            {
                continue;
            }

            var colon = line.LastIndexOf(' ');
            // bcdedit uses "recoveryenabled            Yes"
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                parts[0].Equals("recoveryenabled", StringComparison.OrdinalIgnoreCase))
            {
                return parts[^1].Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                       parts[^1].Equals("True", StringComparison.OrdinalIgnoreCase);
            }

            if (colon >= 0)
            {
                var value = line[(colon + 1)..].Trim();
                if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Interprets <c>winmgmt /verifyrepository</c>. "consistent" is a substring of
    /// "INCONSISTENT", so the inconsistent phrase must win before the healthy phrase.
    /// </summary>
    public static WmiRepositoryState ParseWmiRepositoryState(string? stdout, string? stderr, int exitCode)
    {
        var text = Combine(stdout, stderr);
        if (Contains(text, "inconsistent") ||
            Contains(text, "tutarsız") ||
            Contains(text, "tutarsiz"))
        {
            return WmiRepositoryState.Inconsistent;
        }

        if (Contains(text, "consistent") ||
            Contains(text, "tutarlı") ||
            Contains(text, "tutarli"))
        {
            return WmiRepositoryState.Consistent;
        }

        // No recognizable phrase: trust the exit code only when it is clean.
        return exitCode == 0 ? WmiRepositoryState.Consistent : WmiRepositoryState.Unknown;
    }

    public enum WmiRepositoryState
    {
        Unknown,
        Consistent,
        Inconsistent
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Combine(string? stdout, string? stderr) =>
        string.Concat(stdout ?? string.Empty, "\n", stderr ?? string.Empty);
}
