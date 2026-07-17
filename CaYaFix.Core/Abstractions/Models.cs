// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CaYaFix.Core;

public enum Severity
{
    Info,
    Warning,
    Critical
}

public enum RiskTier
{
    Safe = 1,
    Moderate = 2,
    Aggressive = 3
}

public enum FindingStatus
{
    Open,
    Repairing,
    Resolved,
    Unresolved,
    Skipped
}

public enum BackupKind
{
    Registry,
    File,
    CommandState,
    ServiceState,
    Driver,
    Value,
    Bundle
}

public enum SessionStatus
{
    Created,
    Running,
    PendingVerification,
    Completed,
    Cancelled,
    Failed,
    RolledBack
}

public sealed record CmdResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public sealed record ConsoleLine(
    DateTimeOffset Timestamp,
    string Level,
    string Text,
    bool IsCommand = false);

public sealed class Finding
{
    public required string CheckId { get; init; }
    public required string ModuleId { get; init; }
    public required Severity Severity { get; init; }
    public required string MessageKey { get; init; }
    public object[] MessageArguments { get; init; } = [];
    public string TechnicalDetail { get; init; } = string.Empty;
    public IReadOnlyList<string> RecommendedFixIds { get; init; } = [];
    public Dictionary<string, string> RepairParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public FindingStatus Status { get; set; } = FindingStatus.Open;
}

public sealed record FixResult(
    bool Success,
    string MessageKey,
    string TechnicalDetail = "",
    bool RequiresReboot = false)
{
    public static FixResult Ok(string key, string detail = "", bool reboot = false) =>
        new(true, key, detail, reboot);

    public static FixResult Fail(string key, string detail = "") =>
        new(false, key, detail);
}

public sealed class BackupEntry
{
    public required string Id { get; init; }
    public required BackupKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Location { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string ContentHash { get; init; } = string.Empty;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record FixExecutionResult(
    string FixId,
    string TitleKey,
    string FindingCheckId,
    RiskTier Tier,
    bool Applied,
    bool Verified,
    bool BackupCreated,
    bool RequiresReboot,
    string MessageKey,
    BackupEntry? Backup = null,
    string TechnicalDetail = "");

public sealed record FixRequest(
    Finding Finding,
    FixAction Fix,
    IReadOnlyDictionary<string, string>? Parameters = null,
    DiagnosticCheck? DiagnosticCheck = null);

public sealed record DiagnosticProgress(
    string ModuleId,
    string CheckId,
    string TitleKey,
    int Completed,
    int Total,
    bool? Passed = null);

public sealed record FixProgress(
    string FixId,
    string TitleKey,
    string StageKey,
    int Completed,
    int Total,
    bool? Success = null);

public sealed record TestProgress(
    string TestId,
    string StageKey,
    double Progress,
    string Detail,
    IReadOnlyDictionary<string, double>? Metrics = null,
    bool RequiresUserAnswer = false);

public sealed record Playbook(
    string Id,
    string ModuleId,
    string SymptomKey,
    IReadOnlyList<string> CheckIds,
    IReadOnlyList<string> PreferredFixIds);

public sealed record ModuleInfo(
    string Id,
    string NameKey,
    string DescriptionKey,
    string IconFile,
    int Priority);

public sealed class SessionManifest
{
    public required string Id { get; init; }
    public required string DirectoryPath { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionStatus Status { get; set; } = SessionStatus.Created;
    public bool DryRun { get; init; }
    public bool RestorePointCreated { get; set; }
    public bool PendingVerify { get; set; }
    public bool RequiresReboot { get; set; }
    public List<SessionActionRecord> Actions { get; init; } = [];
    public List<SessionFindingRecord> Findings { get; init; } = [];
}

public sealed class SessionActionRecord
{
    public required string FixId { get; init; }
    public string TitleMessageKey { get; init; } = string.Empty;
    public required string FindingCheckId { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RiskTier Tier { get; init; }
    public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.Now;
    public bool Applied { get; set; }
    public bool Verified { get; set; }
    public bool Undone { get; set; }
    public bool RequiresReboot { get; set; }
    /// <summary>
    /// When true, an applied Aggressive action was allowed to proceed after an explicit
    /// backup failure consent (Tier 3 only). Undo is unavailable without a backup.
    /// </summary>
    public bool BackupOptional { get; set; }
    public string ResultMessageKey { get; set; } = string.Empty;
    public string TechnicalDetail { get; set; } = string.Empty;
    public BackupEntry? Backup { get; set; }
}

/// <summary>
/// Shared recovery-gate rules for write-ahead interrupts and failed automatic rollbacks.
/// </summary>
public static class SessionRecoveryGates
{
    private static readonly HashSet<string> RecoveryRequiredResultKeys = new(StringComparer.Ordinal)
    {
        "FixResult_ApplyInProgress",
        "FixResult_ApplyFailedRollbackFailed",
        "FixResult_VerificationFailedRollbackFailed",
        "FixResult_CancelledRollbackFailed"
    };

    public static bool RequiresRecovery(SessionActionRecord action) =>
        action.Applied &&
        !action.Verified &&
        !action.Undone &&
        RecoveryRequiredResultKeys.Contains(action.ResultMessageKey);

    public static bool SessionRequiresRecovery(SessionManifest session) =>
        session.Actions.Any(RequiresRecovery);
}

public sealed class SessionFindingRecord
{
    public required string CheckId { get; init; }
    public required string ModuleId { get; init; }
    public string ModuleName { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Severity Severity { get; init; }
    public required string MessageKey { get; init; }
    public string UserMessage { get; init; } = string.Empty;
    public string TechnicalDetail { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FindingStatus Status { get; set; }
}

public sealed class Thresholds
{
    private readonly IReadOnlyDictionary<string, double> _values;

    public Thresholds(IReadOnlyDictionary<string, double>? values = null) =>
        _values = values ?? new Dictionary<string, double>();

    public double Get(string key, double fallback) =>
        _values.TryGetValue(key, out var value) ? value : fallback;
}

public sealed class DiagnosticContext
{
    public required ICommandRunner Commands { get; init; }
    public required ITextProvider Text { get; init; }
    public required Thresholds Thresholds { get; init; }
    public string SessionDirectory { get; init; } = Path.GetTempPath();
}

public sealed class FixContext
{
    public required ICommandRunner Commands { get; init; }
    public required IBackupService Backups { get; init; }
    public required IConsoleSink Console { get; init; }
    public required ITextProvider Text { get; init; }
    public required SessionManifest Session { get; init; }
    public bool DryRun { get; init; }
    public bool ForceModeConfirmed { get; init; }
    public bool RestorePointAvailable { get; init; }
    /// <summary>
    /// Tier 3 only: when true, a failed/missing action backup may still proceed after UI consent.
    /// </summary>
    public bool AllowBackuplessAggressive { get; init; }
    public Thresholds Thresholds { get; init; } = new();
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}
