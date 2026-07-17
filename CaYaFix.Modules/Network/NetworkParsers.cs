// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CaYaFix.Modules.Network;

public sealed record PingStatistics(
    int Sent,
    int Received,
    double LossPercent,
    double MinimumMs,
    double AverageMs,
    double MaximumMs,
    double JitterMs);

public sealed record PersistentRouteTarget(
    string DestinationPrefix,
    string NextHop,
    int InterfaceIndex);

public static partial class NetworkParsers
{
    private const int MaximumPingSamples = 10_000;
    private const int MaximumPacketCount = 100_000;
    private const double MaximumReplyMilliseconds = 3_600_000;
    private const int MaximumColonTableEntries = 4_096;
    private const int MaximumColonKeyCharacters = 512;
    private const int MaximumColonValueCharacters = 64 * 1024;
    private const int MaximumRouteTargets = 64;
    private const int MaximumEncodedRouteTargetCharacters = 4_096;

    [GeneratedRegex(@"(?im)^[^\r\n]*?[=<]\s*(\d+)\s*ms[^\r\n]*\bTTL\s*=\s*\d+")]
    private static partial Regex ReplyTimeRegex();

    [GeneratedRegex(@"(?:Sent|Gönderilen)\s*=\s*(\d+).*?(?:Received|Alınan)\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PacketCountRegex();

    [GeneratedRegex(@"(?:%\s*(\d+)|(\d+)\s*%)\s*(?:loss|kayıp)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LossRegex();

    [GeneratedRegex(@"=\s*(\d+)\s*,[^=\r\n]*=\s*(\d+)\s*,[^=\r\n]*=\s*(\d+)\s*\(\s*%?\s*(\d+)\s*%?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageIndependentPacketSummaryRegex();

    public static PingStatistics ParsePing(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var times = new List<double>();
        foreach (Match match in ReplyTimeRegex().Matches(output))
        {
            if (times.Count >= MaximumPingSamples) break;
            if (TryParseBoundedDouble(match.Groups[1].Value, MaximumReplyMilliseconds, out var milliseconds))
            {
                times.Add(milliseconds);
            }
        }

        var summary = LanguageIndependentPacketSummaryRegex().Match(output);
        var packetCounts = PacketCountRegex().Match(output);
        var hasSummary = TryParsePacketSummary(summary, out var summarySent, out var summaryReceived, out var summaryLoss);
        var hasPacketCounts = TryParsePacketCounts(packetCounts, out var packetSent, out var packetReceived);
        var sent = hasSummary
            ? summarySent
            : hasPacketCounts
                ? packetSent
                : times.Count;
        var received = hasSummary
            ? summaryReceived
            : hasPacketCounts
                ? packetReceived
                : times.Count;
        if (sent == 0 && received > 0) sent = received;
        received = Math.Clamp(received, 0, sent);

        var lossMatch = LossRegex().Match(output);
        var lossText = lossMatch.Groups[1].Success ? lossMatch.Groups[1].Value : lossMatch.Groups[2].Value;
        var localizedLoss = 0d;
        var hasLocalizedLoss = lossMatch.Success && TryParseBoundedDouble(lossText, 100, out localizedLoss);
        var loss = hasSummary
            ? summaryLoss
            : hasLocalizedLoss
                ? localizedLoss
                : sent > 0
                    ? (sent - received) * 100d / sent
                    : 100d;
        loss = Math.Clamp(loss, 0, 100);

        var minimum = times.Count == 0 ? 0 : times.Min();
        var average = times.Count == 0 ? 0 : times.Average();
        var maximum = times.Count == 0 ? 0 : times.Max();
        var jitter = times.Count < 2
            ? 0
            : times.Zip(times.Skip(1), (first, second) => Math.Abs(second - first)).Average();
        return new PingStatistics(sent, received, loss, minimum, average, maximum, jitter);
    }

    private static bool TryParsePacketSummary(
        Match match,
        out int sent,
        out int received,
        out double loss)
    {
        sent = 0;
        received = 0;
        loss = 0;
        if (!match.Success ||
            !TryParseBoundedInt(match.Groups[1].Value, out sent) ||
            !TryParseBoundedInt(match.Groups[2].Value, out received) ||
            !TryParseBoundedInt(match.Groups[3].Value, out var lost) ||
            !TryParseBoundedDouble(match.Groups[4].Value, 100, out loss) ||
            received > sent || lost > sent || received + lost != sent)
        {
            sent = 0;
            received = 0;
            loss = 0;
            return false;
        }

        return true;
    }

    private static bool TryParsePacketCounts(Match match, out int sent, out int received)
    {
        sent = 0;
        received = 0;
        if (!match.Success ||
            !TryParseBoundedInt(match.Groups[1].Value, out sent) ||
            !TryParseBoundedInt(match.Groups[2].Value, out received) ||
            received > sent)
        {
            sent = 0;
            received = 0;
            return false;
        }

        return true;
    }

    private static bool TryParseBoundedInt(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
        parsed is >= 0 and <= MaximumPacketCount;

    private static bool TryParseBoundedDouble(string value, double maximum, out double parsed) =>
        double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) &&
        double.IsFinite(parsed) && parsed >= 0 && parsed <= maximum;

    public static IReadOnlyDictionary<string, string> ParseColonTable(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            if (key.Length > MaximumColonKeyCharacters) key = key[..MaximumColonKeyCharacters];
            if (value.Length > MaximumColonValueCharacters) value = value[..MaximumColonValueCharacters];
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
                if (values.Count >= MaximumColonTableEntries) break;
            }
        }

        return values;
    }

    public static double? ParsePercent(IReadOnlyDictionary<string, string> values, params string[] keyFragments)
    {
        var pair = values.FirstOrDefault(item =>
            keyFragments.Any(fragment => item.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(pair.Value))
        {
            return null;
        }

        var digits = new string(pair.Value.Take(MaximumColonKeyCharacters).TakeWhile(character => char.IsDigit(character) || character is '.' or ',').ToArray())
            .Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    public static string EncodeRouteTargets(IEnumerable<PersistentRouteTarget> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var validated = routes
            .Where(IsValidRouteTarget)
            .Distinct()
            .Take(MaximumRouteTargets + 1)
            .ToArray();
        if (validated.Length is 0 or > MaximumRouteTargets) return string.Empty;

        var encoded = string.Join(';', validated.Select(route =>
            $"{route.DestinationPrefix}|{route.NextHop}|{route.InterfaceIndex.ToString(CultureInfo.InvariantCulture)}"));
        return encoded.Length <= MaximumEncodedRouteTargetCharacters ? encoded : string.Empty;
    }

    public static IReadOnlyList<PersistentRouteTarget> ParseRouteTargets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedRouteTargetCharacters)
        {
            return [];
        }

        var segments = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is 0 or > MaximumRouteTargets) return [];

        var result = new List<PersistentRouteTarget>(segments.Length);
        foreach (var segment in segments)
        {
            var fields = segment.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var interfaceIndex))
            {
                return [];
            }

            var route = new PersistentRouteTarget(fields[0], fields[1], interfaceIndex);
            if (!IsValidRouteTarget(route)) return [];
            if (!result.Contains(route)) result.Add(route);
        }

        return result;
    }

    private static bool IsValidRouteTarget(PersistentRouteTarget route)
    {
        if (route.InterfaceIndex <= 0 ||
            string.IsNullOrWhiteSpace(route.DestinationPrefix) || route.DestinationPrefix.Length > 18 ||
            string.IsNullOrWhiteSpace(route.NextHop) || route.NextHop.Length > 15)
        {
            return false;
        }

        var slash = route.DestinationPrefix.LastIndexOf('/');
        if (slash <= 0 || slash == route.DestinationPrefix.Length - 1 ||
            !IPAddress.TryParse(route.DestinationPrefix[..slash], out var network) ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(route.DestinationPrefix[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength) ||
            prefixLength is < 0 or > 32 ||
            !IPAddress.TryParse(route.NextHop, out var nextHop) ||
            nextHop.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return true;
    }
}
