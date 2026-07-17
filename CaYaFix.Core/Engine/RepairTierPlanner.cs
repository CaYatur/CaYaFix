// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

namespace CaYaFix.Core;

public static class RepairTierPlanner
{
    public static RiskTier? NextAvailableTier(
        RiskTier current,
        IEnumerable<RiskTier> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!Enum.IsDefined(current))
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        return candidates
            .Where(candidate => Enum.IsDefined(candidate) && (int)candidate > (int)current)
            .Distinct()
            .OrderBy(candidate => candidate)
            .Select(candidate => (RiskTier?)candidate)
            .FirstOrDefault();
    }
}
