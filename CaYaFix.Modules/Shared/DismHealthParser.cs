// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Modules.Shared;

/// <summary>
/// Interprets DISM /CheckHealth, /ScanHealth, and /AnalyzeComponentStore console output
/// according to Microsoft's documented result phrases (not naive substring matches).
/// </summary>
/// <remarks>
/// Microsoft CheckHealth/ScanHealth outcomes:
/// - "No component store corruption detected" → healthy (do not repair)
/// - "The component store is repairable" → repair with RestoreHealth
/// - "The component store is not repairable" / store corrupted → serious
/// AnalyzeComponentStore "Cleanup Recommended : Yes" is space cleanup, not corruption.
/// </remarks>
public static class DismHealthParser
{
    public enum ComponentStoreHealth
    {
        /// <summary>No corruption; DISM completed successfully.</summary>
        Healthy,

        /// <summary>Corruption found and DISM reports it can be repaired.</summary>
        Repairable,

        /// <summary>Corruption found and DISM cannot repair online.</summary>
        NotRepairable,

        /// <summary>Command failed without a clear healthy/repairable phrase.</summary>
        UnknownFailure
    }

    public static ComponentStoreHealth ParseHealthScan(
        string? stdout,
        string? stderr,
        int exitCode,
        bool timedOut = false)
    {
        var text = Combine(stdout, stderr);

        // Explicit healthy phrase always wins — including when progress bars and
        // "100.0%" appear earlier. Naive "corruption" matching is wrong because
        // "No component store corruption detected" contains that word.
        if (IsHealthyPhrase(text))
        {
            return ComponentStoreHealth.Healthy;
        }

        if (IsNotRepairablePhrase(text))
        {
            return ComponentStoreHealth.NotRepairable;
        }

        if (IsRepairablePhrase(text))
        {
            return ComponentStoreHealth.Repairable;
        }

        if (timedOut)
        {
            return ComponentStoreHealth.UnknownFailure;
        }

        // DISM often returns 0 with "The operation completed successfully" after a clean scan.
        if (exitCode == 0 &&
            (text.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("İşlem başarıyla tamamlandı", StringComparison.OrdinalIgnoreCase)))
        {
            return ComponentStoreHealth.Healthy;
        }

        return exitCode == 0
            ? ComponentStoreHealth.Healthy
            : ComponentStoreHealth.UnknownFailure;
    }

    public static bool NeedsCorruptionRepair(ComponentStoreHealth health) =>
        health is ComponentStoreHealth.Repairable
            or ComponentStoreHealth.NotRepairable
            or ComponentStoreHealth.UnknownFailure;

    /// <summary>
    /// True only when DISM explicitly recommends component-store cleanup.
    /// "Number of Reclaimable Packages : N" alone is not enough (N can be 0).
    /// </summary>
    public static bool IsCleanupRecommended(string? stdout, string? stderr)
    {
        var text = Combine(stdout, stderr);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // English: "Component Store Cleanup Recommended : Yes"
            if (ContainsIgnoreCase(line, "Cleanup Recommended") &&
                EndsWithYes(line))
            {
                return true;
            }

            // Compact variants
            if (ContainsIgnoreCase(line, "Component Store Cleanup Recommended") &&
                ContainsIgnoreCase(line, "Yes") &&
                !ContainsIgnoreCase(line, "No"))
            {
                // Prefer exact Yes after colon when both could appear.
                var colon = line.LastIndexOf(':');
                if (colon >= 0)
                {
                    var value = line[(colon + 1)..].Trim();
                    if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("Evet", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (value.Equals("No", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("Hayır", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
            }
        }

        return false;
    }

    public static int? TryParseReclaimablePackages(string? stdout, string? stderr)
    {
        var text = Combine(stdout, stderr);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!ContainsIgnoreCase(line, "Reclaimable Packages"))
            {
                continue;
            }

            var colon = line.LastIndexOf(':');
            if (colon < 0) continue;
            var value = line[(colon + 1)..].Trim();
            if (int.TryParse(value, out var count) && count >= 0)
            {
                return count;
            }
        }

        return null;
    }

    private static bool IsHealthyPhrase(string text) =>
        ContainsIgnoreCase(text, "No component store corruption detected") ||
        ContainsIgnoreCase(text, "Bileşen deposu bozulması algılanmadı") ||
        ContainsIgnoreCase(text, "Bileşen deposunda bozulma algılanmadı");

    private static bool IsRepairablePhrase(string text)
    {
        // Must not treat "not repairable" as repairable.
        if (IsNotRepairablePhrase(text))
        {
            return false;
        }

        return ContainsIgnoreCase(text, "The component store is repairable") ||
               ContainsIgnoreCase(text, "component store corruption was detected") ||
               ContainsIgnoreCase(text, "Bileşen deposu onarılabilir") ||
               // Legacy / alternate phrasing
               (ContainsIgnoreCase(text, "The component store") &&
                ContainsIgnoreCase(text, "is repairable") &&
                !ContainsIgnoreCase(text, "not repairable"));
    }

    private static bool IsNotRepairablePhrase(string text) =>
        ContainsIgnoreCase(text, "The component store is not repairable") ||
        ContainsIgnoreCase(text, "The component store has been corrupted") ||
        ContainsIgnoreCase(text, "Error: 14098") ||
        ContainsIgnoreCase(text, "Bileşen deposu onarılamaz") ||
        ContainsIgnoreCase(text, "bileşen deposu bozuldu");

    private static bool EndsWithYes(string line)
    {
        var colon = line.LastIndexOf(':');
        if (colon < 0) return false;
        var value = line[(colon + 1)..].Trim();
        return value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Evet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Combine(string? stdout, string? stderr) =>
        string.Concat(stdout ?? string.Empty, "\n", stderr ?? string.Empty);
}
