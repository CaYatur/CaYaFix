// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace CaYaFix.Core;

public sealed class DiagnosticEngine
{
    private const int MaximumTechnicalDetailCharacters = 64 * 1024;
    private const int MaximumMessageArguments = 32;
    private const int MaximumRecommendedFixes = 64;
    private const int MaximumRepairParameters = 64;
    private readonly int _maxParallelism;

    public DiagnosticEngine(int maxParallelism = 4) =>
        _maxParallelism = Math.Clamp(maxParallelism, 1, 8);

    public async Task<IReadOnlyList<Finding>> RunAsync(
        IEnumerable<IModuleDefinition> modules,
        DiagnosticContext context,
        bool quickOnly,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken ct)
    {
        var checks = modules
            .OrderBy(module => module.Info.Priority)
            .SelectMany(module => module.Checks)
            .Where(check => !quickOnly || check.IsQuickCheck)
            .ToArray();

        var findings = new ConcurrentBag<Finding>();
        var completed = 0;
        using var throttler = new SemaphoreSlim(_maxParallelism, _maxParallelism);

        var tasks = checks.Select(check => Task.Run(async () =>
            {
                await throttler.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    progress?.Report(new DiagnosticProgress(
                        check.ModuleId,
                        check.Id,
                        check.TitleKey,
                        Volatile.Read(ref completed),
                        checks.Length));

                    Finding? finding;
                    try
                    {
                        finding = await check.RunAsync(context, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        finding = new Finding
                        {
                            CheckId = check.Id,
                            ModuleId = check.ModuleId,
                            Severity = Severity.Info,
                            MessageKey = "Finding_CheckCouldNotRun",
                            MessageArguments = [context.Text.Get(check.TitleKey)],
                            TechnicalDetail = ex.ToString()
                        };
                    }

                    if (finding is not null)
                    {
                        finding = NormalizeFinding(check, finding);
                        findings.Add(finding);
                    }

                    var current = Interlocked.Increment(ref completed);
                    progress?.Report(new DiagnosticProgress(
                        check.ModuleId,
                        check.Id,
                        check.TitleKey,
                        current,
                        checks.Length,
                        finding is null));
                }
                finally
                {
                    throttler.Release();
                }
            }, ct));

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.ModuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.CheckId, StringComparer.Ordinal)
            .ToArray();
    }

    private static Finding NormalizeFinding(DiagnosticCheck check, Finding finding)
    {
        var recommendations = finding.RecommendedFixIds
            .Where(IsValidIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecommendedFixes)
            .ToArray();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in finding.RepairParameters)
        {
            if (parameters.Count >= MaximumRepairParameters) break;
            if (!IsValidIdentifier(pair.Key) || pair.Value is null || pair.Value.Length > 4_096 ||
                pair.Value.IndexOf('\0') >= 0)
            {
                continue;
            }
            parameters.TryAdd(pair.Key, pair.Value);
        }

        var messageKey = IsValidIdentifier(finding.MessageKey)
            ? finding.MessageKey
            : "Finding_CheckCouldNotRun";
        return new Finding
        {
            CheckId = check.Id,
            ModuleId = check.ModuleId,
            Severity = Enum.IsDefined(finding.Severity) ? finding.Severity : Severity.Info,
            MessageKey = messageKey,
            MessageArguments = finding.MessageArguments
                .Take(MaximumMessageArguments)
                .Select(argument => argument is string text ? BoundText(text, 4_096) : argument)
                .ToArray(),
            TechnicalDetail = BoundText(finding.TechnicalDetail, MaximumTechnicalDetailCharacters),
            RecommendedFixIds = recommendations,
            RepairParameters = parameters,
            Status = Enum.IsDefined(finding.Status) ? finding.Status : FindingStatus.Open
        };
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value.IndexOf('\0') < 0;

    private static string BoundText(string? value, int maximumLength)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ');
        return normalized.Length <= maximumLength
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumLength - 25), "\n[truncated by CaYaFix]");
    }
}
