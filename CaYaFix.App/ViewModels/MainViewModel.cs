// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CaYaFix.Core;
using CaYaFix.App.Properties;

namespace CaYaFix.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaximumStoredDetailCharacters = 64 * 1024;
    private const int MaximumPendingConsoleLines = 2_000;
    private const int MaximumVisibleConsoleLines = 750;
    private const int ConsoleFlushBatchSize = 250;
    private const int MaximumRecoverySessionsShown = 100;
    private readonly IReadOnlyList<IModuleDefinition> _modules;
    private readonly DiagnosticEngine _diagnostics;
    private readonly FixEngine _fixes;
    private readonly ISessionManager _sessions;
    private readonly IBackupService _backups;
    private readonly RestorePointService _restorePoints;
    private readonly HtmlReportService _reports;
    private readonly SupportPackageService _support;
    private readonly ICommandRunner _commands;
    private readonly EventConsoleSink _console;
    private readonly ITextProvider _text;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _restorePointCts;
    private SessionManifest? _currentSession;
    private IModuleDefinition? _selectedModule;
    private bool _restorePointAttempted;
    private bool _restorePointCreated;
    /// <summary>User explicitly accepted continuing without a System Restore point.</summary>
    private bool _restorePointBypassAccepted;
    private bool _startupComplete;
    private bool _languageSelectionReady;
    private bool _isRollbackOperation;
    private DateTimeOffset _operationStartedAt;
    private double _progressFloor;
    private double _progressCeiling = 100;
    private double _toolPercent;
    private double _expectedToolMinutes;
    private string _currentFixId = string.Empty;
    private string _currentStageKey = string.Empty;
    private DispatcherTimer? _progressTicker;
    private static readonly Regex ProgressPercentRegex = new(
        @"(?<!\d)(?<p>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProgressVerificationRegex = new(
        @"(?:Verification|Doğrulama|verification)\s+(?<p>\d{1,3})\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly ConcurrentQueue<ConsoleLine> _pendingConsoleLines = new();
    private int _pendingConsoleLineCount;
    private int _consoleFlushScheduled;

    public MainViewModel(
        IReadOnlyList<IModuleDefinition> modules,
        DiagnosticEngine diagnostics,
        FixEngine fixes,
        ISessionManager sessions,
        IBackupService backups,
        RestorePointService restorePoints,
        HtmlReportService reports,
        SupportPackageService support,
        ICommandRunner commands,
        EventConsoleSink console,
        ITextProvider text)
    {
        _modules = modules;
        _diagnostics = diagnostics;
        _fixes = fixes;
        _sessions = sessions;
        _backups = backups;
        _restorePoints = restorePoints;
        _reports = reports;
        _support = support;
        _commands = commands;
        _console = console;
        _text = text;
        var version = typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 1, 0);
        VersionLabel = text.Get("App_VersionFormat", version.Major, version.Minor, Math.Max(0, version.Build));

        StatusText = text.Get("Status_Ready");
        RepairButtonText = text.Get("Action_RepairSelected");
        Modules = new ObservableCollection<ModuleCardViewModel>(
            modules.Select(module => new ModuleCardViewModel(
                module,
                text,
                ToggleModuleCardAsync,
                ScanModuleCardAsync)));
        LiveTests = new ObservableCollection<LiveTestItemViewModel>(
            modules.SelectMany(module => module.LiveTests.Select(test =>
                new LiveTestItemViewModel(test, text.Get(module.Info.NameKey), text, RunLiveTestAsync))));
        ExpertFixes = new ObservableCollection<ExpertFixViewModel>(
            modules
                .OrderBy(module => module.Info.Priority)
                .SelectMany(module => module.Fixes
                    .OrderBy(fix => fix.Tier)
                    .ThenBy(fix => text.Get(fix.TitleKey), StringComparer.CurrentCultureIgnoreCase)
                    .Select(fix => new ExpertFixViewModel(
                        fix,
                        text.Get(module.Info.NameKey),
                        text,
                        RunExpertFixAsync))));

        // Manual toolkit: no target required so users can pick and run Microsoft-oriented tools directly.
        ManualTools = new ObservableCollection<ExpertFixViewModel>(
            modules
                .OrderBy(module => module.Info.Priority)
                .SelectMany(module => module.Fixes
                    .Where(fix => !fix.RequiresTargetParameter)
                    .OrderBy(fix => fix.Tier)
                    .ThenBy(fix => text.Get(fix.TitleKey), StringComparer.CurrentCultureIgnoreCase)
                    .Select(fix => new ExpertFixViewModel(
                        fix,
                        text.Get(module.Info.NameKey),
                        text,
                        RunExpertFixAsync))));

        RebuildGuidedSymptoms();
        RefreshLanguageOptions(selectCurrent: true);
        _languageSelectionReady = true;
        _console.LineWritten += OnConsoleLine;
    }

    private void RebuildGuidedSymptoms()
    {
        GuidedSymptoms.Clear();
        foreach (var module in _modules.OrderBy(item => item.Info.Priority))
        {
            var moduleName = _text.Get(module.Info.NameKey);
            var moduleIcon = ResolveModuleIcon(module.Info.Id);
            foreach (var playbook in module.Playbooks)
            {
                var fixTitles = ResolvePlaybookFixes(module, playbook)
                    .Select(fix => _text.Get(fix.TitleKey))
                    .ToArray();
                GuidedSymptoms.Add(new GuidedSymptomItemViewModel(
                    playbook,
                    moduleName,
                    moduleIcon,
                    fixTitles,
                    ResolveModuleSideEffects(module.Info.Id),
                    _text,
                    OpenGuidedRepairConfirmAsync));
            }
        }
    }

    private IReadOnlyList<FixAction> ResolvePlaybookFixes(IModuleDefinition module, Playbook playbook)
    {
        var preferred = playbook.PreferredFixIds
            .Select(id => module.Fixes.FirstOrDefault(fix =>
                fix.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(fix => fix is not null)
            .Cast<FixAction>()
            // Guided repair stays on Safe/Moderate — Aggressive needs the force path separately.
            .Where(fix => fix.Tier is RiskTier.Safe or RiskTier.Moderate)
            .Where(fix => !fix.RequiresTargetParameter)
            .GroupBy(fix => fix.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (preferred.Length > 0) return preferred;

        return module.Fixes
            .Where(fix => fix.Tier is RiskTier.Safe or RiskTier.Moderate)
            .Where(fix => !fix.RequiresTargetParameter)
            .OrderBy(fix => fix.Tier)
            .ThenBy(fix => fix.Id, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private string ResolveModuleSideEffects(string moduleId) =>
        _text.Get(moduleId.ToLowerInvariant() switch
        {
            "network" => "GuidedRepair_Effects_Network",
            "audio" => "GuidedRepair_Effects_Audio",
            "display" or "display-graphics" => "GuidedRepair_Effects_Display",
            "bluetooth" => "GuidedRepair_Effects_Bluetooth",
            "printers" or "printer" => "GuidedRepair_Effects_Printer",
            "disk" or "disk-storage" => "GuidedRepair_Effects_Disk",
            "integrity" => "GuidedRepair_Effects_Integrity",
            "windows-update" or "update" => "GuidedRepair_Effects_Update",
            "store" or "store-apps" => "GuidedRepair_Effects_Store",
            "camera" or "camera-privacy" => "GuidedRepair_Effects_Camera",
            "usb" or "usb-devices" => "GuidedRepair_Effects_Usb",
            "search" or "windows-search" => "GuidedRepair_Effects_Search",
            "time" or "time-sync" => "GuidedRepair_Effects_Time",
            "startup" or "startup-performance" or "performance" => "GuidedRepair_Effects_Performance",
            "boot" => "GuidedRepair_Effects_Boot",
            _ => "GuidedRepair_Effects_Generic"
        });

    private void RefreshLanguageOptions(bool selectCurrent)
    {
        var previousReady = _languageSelectionReady;
        _languageSelectionReady = false;
        Languages.Clear();
        Languages.Add(new LanguageOption(AppLanguage.Auto, _text.Get("Language_System")));
        Languages.Add(new LanguageOption(AppLanguage.English, _text.Get("Language_English")));
        Languages.Add(new LanguageOption(AppLanguage.Turkish, _text.Get("Language_Turkish")));
        if (selectCurrent)
        {
            SelectedLanguage = Languages.FirstOrDefault(option =>
                option.Code.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase))
                ?? Languages[0];
        }
        _languageSelectionReady = previousReady;
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (!_languageSelectionReady ||
            value is null ||
            value.Code.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Defer so ComboBox selection can settle before modal restart confirmation.
        Application.Current?.Dispatcher.BeginInvoke(() => RequestLanguageChange(value.Code));
    }

    private void RequestLanguageChange(string preference)
    {
        if (IsBusy)
        {
            SelectedLanguage = Languages.FirstOrDefault(option =>
                option.Code.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase));
            StatusText = _text.Get("Status_Failed", _text.Get("Status_Cancelled"));
            return;
        }

        if (!AppLanguage.IsSupportedPreference(preference) ||
            preference.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var confirm = MessageBox.Show(
            _text.Get("Dialog_LanguageRestart"),
            _text.Get("Dialog_LanguageRestart_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            SelectedLanguage = Languages.FirstOrDefault(option =>
                option.Code.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase));
            return;
        }

        try
        {
            AppLanguage.SavePreference(preference);
            StatusText = _text.Get("Status_LanguageSaved");
            RestartApplication();
        }
        catch (Exception exception)
        {
            StatusText = _text.Get("Status_Failed", exception.Message);
            SelectedLanguage = Languages.FirstOrDefault(option =>
                option.Code.Equals(AppLanguage.Preference, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void RestartApplication()
    {
        // Must release the global mutex before spawning the replacement process.
        App.ReleaseSingleInstanceForRestart();

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            try { executable = Process.GetCurrentProcess().MainModule?.FileName; }
            catch { executable = null; }
        }

        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
            });
        }

        Application.Current?.Shutdown(0);
    }

    public ObservableCollection<ModuleCardViewModel> Modules { get; }
    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<LiveTestItemViewModel> LiveTests { get; }
    public ObservableCollection<ExpertFixViewModel> ExpertFixes { get; }
    public ObservableCollection<ExpertFixViewModel> ManualTools { get; }
    public ObservableCollection<SymptomViewModel> Symptoms { get; } = [];
    public ObservableCollection<GuidedSymptomItemViewModel> GuidedSymptoms { get; } = [];
    public ObservableCollection<string> GuidedRepairFixList { get; } = [];
    public ObservableCollection<RecoverySessionViewModel> RecoverySessions { get; } = [];
    public ObservableCollection<ConsoleLineViewModel> ConsoleLines { get; } = [];
    public ObservableCollection<string> TestOutput { get; } = [];
    public ObservableCollection<LiveMetricRowViewModel> LiveMetricRows { get; } = [];
    public ObservableCollection<OperationFeedItem> OperationFeed { get; } = [];
    public ObservableCollection<ToastNotificationViewModel> Toasts { get; } = [];
    public string VersionLabel { get; }

    [ObservableProperty] private int currentPage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool consoleExpanded = true;
    [ObservableProperty] private bool dryRun;
    [ObservableProperty] private bool expertMode;
    [ObservableProperty] private bool riskAccepted;
    [ObservableProperty] private bool showEscalation;
    [ObservableProperty] private bool showHandoff;
    [ObservableProperty] private bool isForceStage;
    [ObservableProperty] private double progressValue;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string activeOperation = string.Empty;
    [ObservableProperty] private string operationTitle = string.Empty;
    [ObservableProperty] private string operationDetail = string.Empty;
    [ObservableProperty] private string operationIcon = "search.svg";
    [ObservableProperty] private string operationPhase = string.Empty;
    [ObservableProperty] private string repairButtonText = string.Empty;
    [ObservableProperty] private string escalationText = string.Empty;
    [ObservableProperty] private string selectedModuleName = string.Empty;
    [ObservableProperty] private string selectedModuleDescription = string.Empty;
    [ObservableProperty] private string selectedModuleIcon = "home.svg";
    [ObservableProperty] private bool hasSelectedModule;
    [ObservableProperty] private RiskTier currentTier = RiskTier.Safe;
    [ObservableProperty] private string? reportPath;
    [ObservableProperty] private bool recoveryRequired;
    [ObservableProperty] private bool showVerificationSummary;
    [ObservableProperty] private string verificationSummaryTitle = string.Empty;
    [ObservableProperty] private string verificationSummaryText = string.Empty;
    [ObservableProperty] private bool showOperationSummary;
    [ObservableProperty] private string operationSummaryHeadline = string.Empty;
    [ObservableProperty] private string operationSummaryDetail = string.Empty;
    [ObservableProperty] private string operationSummaryAdvice = string.Empty;
    [ObservableProperty] private int operationPassedCount;
    [ObservableProperty] private int operationFindingCount;
    [ObservableProperty] private int operationCommonFindingCount;
    [ObservableProperty] private int operationActionableFindingCount;
    [ObservableProperty] private string operationResultIcon = "check.svg";
    [ObservableProperty] private bool canSkipRestorePoint;
    [ObservableProperty] private string progressPercentText = "0%";
    [ObservableProperty] private string progressEtaText = string.Empty;
    [ObservableProperty] private string progressStatusText = string.Empty;
    [ObservableProperty] private bool showGuidedRepairConfirm;
    [ObservableProperty] private bool guidedRepairRiskAccepted;
    [ObservableProperty] private string guidedRepairTitle = string.Empty;
    [ObservableProperty] private string guidedRepairModule = string.Empty;
    [ObservableProperty] private string guidedRepairIcon = "alert.svg";
    [ObservableProperty] private string guidedRepairWarning = string.Empty;
    [ObservableProperty] private string guidedRepairSideEffects = string.Empty;
    [ObservableProperty] private string guidedRepairFixesSummary = string.Empty;
    private GuidedSymptomItemViewModel? _pendingGuidedSymptom;

    public bool ShowOperationOverlay => IsBusy || ShowOperationSummary;

    public bool HasReport => !string.IsNullOrWhiteSpace(ReportPath) && File.Exists(ReportPath);
    public ObservableCollection<LanguageOption> Languages { get; } = [];
    public string CurrentLanguageLabel =>
        $"{AppLanguage.DisplayName(AppLanguage.Preference, _text)} · {AppLanguage.Current.NativeName}";
    public string CurrentTierLabel => _text.Get($"Tier_{CurrentTier}");
    public string ModuleCountLabel => _text.Get("Dashboard_ModuleCount", Modules.Count);
    public string RecoveryBannerTitle => _text.Get("Dialog_InterruptedRepair_Title");
    public string RecoveryBannerText => _text.Get("Dialog_InterruptedRepair");
    public string HandoffTitle => _text.Get("Handoff_Title");
    public string HandoffDescription => _text.Get("Handoff_Description");
    public string HandoffNextSteps => _text.Get("Handoff_NextSteps");

    [ObservableProperty] private LanguageOption? selectedLanguage;

    partial void OnReportPathChanged(string? value) => OnPropertyChanged(nameof(HasReport));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowOperationOverlay));
    partial void OnShowOperationSummaryChanged(bool value) => OnPropertyChanged(nameof(ShowOperationOverlay));

    partial void OnCurrentPageChanged(int value)
    {
        // Module detail is a home overlay; dismiss it whenever the user leaves or jumps pages
        // (e.g. "View findings" sets CurrentPage directly without NavigateCommand).
        if (HasSelectedModule)
        {
            CloseModulePanel();
        }

        if (ShowGuidedRepairConfirm && value != 1)
        {
            CancelGuidedRepair();
        }
    }

    partial void OnDryRunChanged(bool value)
    {
        if (IsBusy || _currentSession is null || _currentSession.DryRun == value) return;

        _currentSession = null;
        ResetRestorePointState();
        Findings.Clear();
        TestOutput.Clear();
        LiveMetricRows.Clear();
        ReportPath = null;
        ShowEscalation = false;
        ShowHandoff = false;
        RiskAccepted = false;
        CurrentTier = RiskTier.Safe;
        StatusText = _text.Get("Status_ModeChangedRescan");
    }

    partial void OnCurrentTierChanged(RiskTier value)
    {
        OnPropertyChanged(nameof(CurrentTierLabel));
        RepairButtonText = value switch
        {
            RiskTier.Moderate => _text.Get("Action_TryModerate"),
            RiskTier.Aggressive => _text.Get("Action_ForceRepair"),
            _ => _text.Get("Action_RepairSelected")
        };
        IsForceStage = value == RiskTier.Aggressive;
        EscalationText = value == RiskTier.Aggressive
            ? _text.Get("Escalation_ForceText")
            : _text.Get("Escalation_ModerateText");
    }

    public async Task InitializeAsync()
    {
        if (_startupComplete || IsBusy) return;

        SessionManifest? pendingVerifySession = null;
        BeginOperation(_text.Get("Status_Starting"));
        try
        {
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
            var pending = await _sessions.GetPendingAsync(CancellationToken.None).ConfigureAwait(true);
            if (pending is null)
            {
                StatusText = _text.Get("Status_Ready");
                return;
            }

            if (SessionRecoveryGates.SessionRequiresRecovery(pending))
            {
                CurrentPage = 3;
                StatusText = _text.Get("Dialog_InterruptedRepair");
                MessageBox.Show(
                    StatusText,
                    _text.Get("Dialog_InterruptedRepair_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var pendingActions = pending.Actions
                .Where(action => action.Applied && action.RequiresReboot && !action.Verified && !action.Undone)
                .ToArray();
            if (pendingActions.Length == 0)
            {
                pending.PendingVerify = false;
                pending.RequiresReboot = false;
                if (pending.Status is SessionStatus.PendingVerification or SessionStatus.Running)
                {
                    pending.Status = SessionStatus.Completed;
                    pending.CompletedAt ??= DateTimeOffset.Now;
                }
                await _sessions.SaveAsync(pending, CancellationToken.None).ConfigureAwait(true);
                await LoadRecoverySessionsAsync().ConfigureAwait(true);
                StatusText = _text.Get("Status_Ready");
                return;
            }

            if (!HasRestartedSincePendingAction(pending))
            {
                MessageBox.Show(
                    _text.Get("Dialog_Pending"),
                    _text.Get("Dialog_Pending_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                StatusText = _text.Get("Dialog_Pending");
                return;
            }

            var verify = MessageBox.Show(
                _text.Get("Dialog_Pending_Verify"),
                _text.Get("Dialog_Pending_Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (verify == MessageBoxResult.Yes)
            {
                pendingVerifySession = pending;
                return;
            }

            StatusText = _text.Get("Dialog_Pending");
        }
        catch (Exception exception)
        {
            StatusText = _text.Get("Status_Failed", exception.Message);
            _console.Write(new ConsoleLine(DateTimeOffset.Now, "ERR", StatusText));
        }
        finally
        {
            if (IsBusy) EndOperation();
            _startupComplete = true;
            if (string.IsNullOrWhiteSpace(StatusText) ||
                StatusText.Equals(_text.Get("Status_Starting"), StringComparison.Ordinal))
            {
                StatusText = _text.Get("Status_Ready");
            }
        }

        if (pendingVerifySession is not null)
        {
            await VerifyPendingRepairsAsync(pendingVerifySession).ConfigureAwait(true);
        }
    }

    private async Task VerifyPendingRepairsAsync(SessionManifest session)
    {
        if (IsBusy) return;
        BeginOperation(_text.Get("Status_VerifyingPending"));
        var operationToken = _operationCts!.Token;
        try
        {
            _currentSession = session;
            Findings.Clear();
            var pendingGroups = session.Actions
                .Where(action => action.Applied && action.RequiresReboot && !action.Verified && !action.Undone)
                .GroupBy(action => action.FindingCheckId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var context = new DiagnosticContext
            {
                Commands = _commands,
                Text = _text,
                Thresholds = LoadThresholds(),
                SessionDirectory = session.DirectoryPath
            };

            var completed = 0;
            var rolledBackAfterVerification = false;
            foreach (var actionGroup in pendingGroups)
            {
                operationToken.ThrowIfCancellationRequested();
                var check = _modules
                    .SelectMany(module => module.Checks)
                    .FirstOrDefault(candidate => candidate.Id.Equals(
                        actionGroup.Key,
                        StringComparison.OrdinalIgnoreCase));
                if (check is null)
                {
                    _console.Write(new ConsoleLine(
                        DateTimeOffset.Now,
                        "WARN",
                        _text.Get("Console_PendingCheckMissing", actionGroup.Key)));
                    // Orphaned reboot actions cannot be rechecked after catalog changes; restore backups.
                    if (await RollbackPendingActionGroupAsync(actionGroup, session).ConfigureAwait(true))
                    {
                        rolledBackAfterVerification = true;
                    }

                    completed++;
                    ProgressValue = pendingGroups.Length == 0 ? 100 : completed * 100d / pendingGroups.Length;
                    continue;
                }

                ActiveOperation = $"{_text.Get(check.TitleKey)} · {completed + 1}/{pendingGroups.Length}";
                Finding? followUp;
                try
                {
                    followUp = await check.RunAsync(context, operationToken).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _console.Write(new ConsoleLine(
                        DateTimeOffset.Now,
                        "ERR",
                        _text.Get("Console_PendingCheckFailed", actionGroup.Key, exception.Message)));
                    if (await RollbackPendingActionGroupAsync(actionGroup, session).ConfigureAwait(true))
                    {
                        rolledBackAfterVerification = true;
                    }

                    completed++;
                    ProgressValue = pendingGroups.Length == 0 ? 100 : completed * 100d / pendingGroups.Length;
                    continue;
                }

                if (followUp is null)
                {
                    foreach (var action in actionGroup)
                    {
                        action.Verified = true;
                        action.ResultMessageKey = "FixResult_Verified";
                    }

                    foreach (var stored in session.Findings.Where(finding =>
                                 finding.CheckId.Equals(actionGroup.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        stored.Status = FindingStatus.Resolved;
                    }
                }
                else
                {
                    followUp.Status = FindingStatus.Unresolved;
                    var module = _modules.FirstOrDefault(item =>
                        item.Info.Id.Equals(followUp.ModuleId, StringComparison.OrdinalIgnoreCase));
                    if (module is not null)
                    {
                        Findings.Add(new FindingViewModel(
                            followUp,
                            _text.Get(module.Info.NameKey),
                            module.Fixes,
                            _text));
                    }

                    var stored = session.Findings.FirstOrDefault(finding =>
                        finding.CheckId.Equals(followUp.CheckId, StringComparison.OrdinalIgnoreCase));
                    if (stored is not null)
                    {
                        stored.Status = FindingStatus.Unresolved;
                    }
                    else
                    {
                        session.Findings.Add(ToSessionFinding(followUp));
                    }

                    if (await RollbackPendingActionGroupAsync(actionGroup, session).ConfigureAwait(true))
                    {
                        rolledBackAfterVerification = true;
                    }
                }

                completed++;
                ProgressValue = pendingGroups.Length == 0 ? 100 : completed * 100d / pendingGroups.Length;
            }

            session.PendingVerify = session.Actions.Any(action =>
                action.Applied && action.RequiresReboot && !action.Verified && !action.Undone);
            session.RequiresReboot = session.PendingVerify;
            var hasActiveActions = session.Actions.Any(action => action.Applied && !action.Undone);
            session.Status = session.PendingVerify
                ? SessionStatus.PendingVerification
                : hasActiveActions
                    ? SessionStatus.Completed
                    : rolledBackAfterVerification
                        ? SessionStatus.RolledBack
                        : SessionStatus.Completed;
            if (!session.PendingVerify) session.CompletedAt = DateTimeOffset.Now;

            await _sessions.SaveAsync(session, operationToken).ConfigureAwait(true);
            ReportPath = await _reports.CreateAsync(session, _text, operationToken).ConfigureAwait(true);
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
            if (RecoveryRequired)
            {
                CurrentPage = 3;
                StatusText = RecoveryBannerText;
            }
            else
            {
                StatusText = _text.Get(session.PendingVerify
                    ? "Status_PendingStillDetected"
                    : rolledBackAfterVerification
                        ? "Status_PendingRolledBack"
                        : "Status_PendingVerified");
                if (Findings.Count > 0) CurrentPage = 1;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
        }
        catch (Exception exception)
        {
            StatusText = _text.Get("Status_Failed", exception.Message);
        }
        finally
        {
            try
            {
                await LoadRecoverySessionsAsync().ConfigureAwait(true);
            }
            catch
            {
                // Non-fatal refresh.
            }

            EndOperation();
        }
    }

    public void LoadReadmeDemo(int page)
    {
        CurrentPage = page;
        RecoveryRequired = false;
        IsBusy = false;
        _startupComplete = true;
        ProgressValue = 0;
        ShowEscalation = false;
        ShowHandoff = false;
        CurrentTier = RiskTier.Safe;
        StatusText = _text.Get("Status_Ready");

        if (page == 1)
        {
            Findings.Clear();
            AddDemoFinding(new Finding
            {
                CheckId = "net.dns",
                ModuleId = "network",
                Severity = Severity.Critical,
                MessageKey = "Finding_Network_CurrentDnsBroken",
                TechnicalDetail = "Configured DNS: 192.168.1.1 (timeout)\n1.1.1.1: 24 ms\n8.8.8.8: 31 ms",
                RecommendedFixIds = ["net.flush-dns", "net.public-dns"]
            });
            AddDemoFinding(new Finding
            {
                CheckId = "audio.defaults",
                ModuleId = "audio",
                Severity = Severity.Warning,
                MessageKey = "Finding_Audio_VirtualDefaultInactive",
                TechnicalDetail = "Default: Voicemeeter Input\nState: Active\nCompanion process: Not running",
                RecommendedFixIds = ["audio.set-default", "audio.restart-services"],
                RepairParameters = { ["audio.set-default.target"] = "{demo-physical-output}" }
            });
            AddDemoFinding(new Finding
            {
                CheckId = "update.errors",
                ModuleId = "windows-update",
                Severity = Severity.Warning,
                MessageKey = "Finding_Update_RecentErrors",
                TechnicalDetail = "0x80070002 · Windows Update Client · 2 recent events",
                RecommendedFixIds = ["update.restart-services", "update.reset-cache"]
            });
            StatusText = _text.Get("Status_ScanComplete", Findings.Count);
        }
        else if (page == 2)
        {
            TestOutput.Clear();
            LiveMetricRows.Clear();
            TestOutput.Add("Ethernet to 1.1.1.1: 0% loss · 18.4 ms · jitter 1.8 ms");
            TestOutput.Add("Wi-Fi to 8.8.8.8: 10% loss · 26.7 ms · jitter 4.2 ms");
            TestOutput.Add("Cloudflare DNS: 21 ms · Google DNS: 29 ms");
            LiveMetricRows.Add(new LiveMetricRowViewModel(
                "net.live.ping",
                _text.Get("LiveMetrics_PingTest"),
                "Ethernet · 1.1.1.1",
                _text.Get("LiveMetrics_Ping", 10, 10, 0, 16.1, 18.4, 21.7, 1.8),
                Severity.Info));
            LiveMetricRows.Add(new LiveMetricRowViewModel(
                "net.live.ping",
                _text.Get("LiveMetrics_PingTest"),
                "Wi-Fi · 8.8.8.8",
                _text.Get("LiveMetrics_Ping", 10, 9, 10, 21.2, 26.7, 34.1, 4.2),
                Severity.Warning));
            LiveMetricRows.Add(new LiveMetricRowViewModel(
                "net.live.dns",
                _text.Get("LiveMetrics_DnsTest"),
                "1.1.1.1",
                _text.Get("LiveMetrics_Dns", 21, _text.Get("TestResult_Passed")),
                Severity.Info));
            foreach (var test in LiveTests)
            {
                var isPing = test.Test.Id == "net.live.ping";
                var isMicrophone = test.Test.Id == "audio.live.microphone";
                test.IsRunning = isPing || isMicrophone;
                test.Progress = isPing ? 64 : isMicrophone ? 48 : 0;
                test.LastResult = isPing
                    ? "Test in progress · 0% loss · 18.4 ms"
                    : isMicrophone
                        ? "Sampling in memory · signal detected · no recording saved"
                        : string.Empty;
            }
        }
    }

    private void AddDemoFinding(Finding finding)
    {
        var module = _modules.First(item => item.Info.Id == finding.ModuleId);
        Findings.Add(new FindingViewModel(finding, _text.Get(module.Info.NameKey), module.Fixes, _text));
    }

    [RelayCommand]
    private async Task AutomaticScanAsync()
    {
        await ScanAsync(_modules, quickOnly: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_operationCts is null || !_operationCts.Token.CanBeCanceled || IsBusy is false)
        {
            return;
        }

        if (_isRollbackOperation)
        {
            var consent = MessageBox.Show(
                _text.Get("Dialog_CancelRollback"),
                _text.Get("Dialog_CancelRollback_Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (consent != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _operationCts.Cancel();
    }

    public void RequestCancellation() => Cancel();

    [RelayCommand]
    private async Task NavigateAsync(string? page)
    {
        if (!int.TryParse(page, out var target)) return;
        CurrentPage = target;
        if (target == 3) await LoadRecoverySessionsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleConsole() => ConsoleExpanded = !ConsoleExpanded;

    [RelayCommand]
    private void SetRiskTier(string? tier)
    {
        if (!ExpertMode || !int.TryParse(tier, out var value) || value is < 1 or > 3) return;
        CurrentTier = (RiskTier)value;
        ShowEscalation = CurrentTier != RiskTier.Safe;
        RiskAccepted = false;
    }

    [RelayCommand]
    private async Task ScanSelectedModuleAsync()
    {
        if (_selectedModule is not null) await ScanAsync([_selectedModule], quickOnly: false).ConfigureAwait(true);
    }

    private Task ToggleModuleCardAsync(ModuleCardViewModel card)
    {
        if (card.IsExpanded)
        {
            card.IsExpanded = false;
            if (ReferenceEquals(_selectedModule, card.Module))
            {
                ClearSelectedModule();
            }

            return Task.CompletedTask;
        }

        foreach (var other in Modules)
        {
            other.IsExpanded = false;
        }

        _selectedModule = card.Module;
        SelectedModuleName = card.Name;
        SelectedModuleDescription = card.Description;
        SelectedModuleIcon = card.IconPath;
        Symptoms.Clear();
        // Symptom chips open guided repair (apply related fixes with risk warning), not only a re-scan.
        card.LoadSymptoms(card.Module.Playbooks, OpenGuidedRepairFromPlaybookAsync);
        foreach (var symptom in card.Symptoms)
        {
            Symptoms.Add(symptom);
        }

        card.IsExpanded = true;
        HasSelectedModule = true;
        return Task.CompletedTask;
    }

    private Task OpenGuidedRepairFromPlaybookAsync(Playbook playbook)
    {
        var item = GuidedSymptoms.FirstOrDefault(candidate =>
            candidate.Playbook.Id.Equals(playbook.Id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            // Fallback: focused scan if the catalog entry is missing.
            return ScanPlaybookAsync(playbook);
        }

        return OpenGuidedRepairConfirmAsync(item);
    }

    [RelayCommand]
    private void CloseModulePanel()
    {
        foreach (var card in Modules.Where(item => item.IsExpanded))
        {
            card.IsExpanded = false;
        }

        ClearSelectedModule();
    }

    private void ClearSelectedModule()
    {
        _selectedModule = null;
        HasSelectedModule = false;
        SelectedModuleName = string.Empty;
        SelectedModuleDescription = string.Empty;
        SelectedModuleIcon = "home.svg";
        Symptoms.Clear();
    }

    private async Task ScanModuleCardAsync(ModuleCardViewModel card)
    {
        _selectedModule = card.Module;
        await ScanAsync([card.Module], quickOnly: false).ConfigureAwait(true);
    }

    private async Task ScanPlaybookAsync(Playbook playbook)
    {
        var module = _modules.First(item => item.Info.Id == playbook.ModuleId);
        var filtered = new FilteredModuleDefinition(module, playbook.CheckIds);
        await ScanAsync([filtered], quickOnly: false).ConfigureAwait(true);
    }

    private Task OpenGuidedRepairConfirmAsync(GuidedSymptomItemViewModel item)
    {
        if (IsBusy || !EnsureReadyForUserWork()) return Task.CompletedTask;

        _pendingGuidedSymptom = item;
        GuidedRepairTitle = item.Title;
        GuidedRepairModule = item.ModuleName;
        GuidedRepairIcon = item.ModuleIcon;
        GuidedRepairWarning = _text.Get("GuidedRepair_Warning");
        GuidedRepairSideEffects = item.SideEffects;
        GuidedRepairFixesSummary = item.FixTitles.Count == 0
            ? _text.Get("GuidedRepair_NoFixes")
            : string.Join(Environment.NewLine, item.FixTitles.Select(title => "• " + title));
        GuidedRepairFixList.Clear();
        foreach (var title in item.FixTitles)
        {
            GuidedRepairFixList.Add(title);
        }

        GuidedRepairRiskAccepted = false;
        ShowGuidedRepairConfirm = true;
        ShowOperationSummary = false;
        CloseModulePanel();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelGuidedRepair()
    {
        ShowGuidedRepairConfirm = false;
        GuidedRepairRiskAccepted = false;
        _pendingGuidedSymptom = null;
        GuidedRepairFixList.Clear();
    }

    [RelayCommand]
    private void OpenGuidedRepairFromSummary()
    {
        ShowOperationSummary = false;
        CloseModulePanel();
        CurrentPage = 1;
        StatusText = _text.Get("GuidedRepair_PickHint");
        PushToast(
            _text.Get("GuidedRepair_ToastTitle"),
            _text.Get("GuidedRepair_PickHint"),
            "alert.svg",
            "warning");
    }

    [RelayCommand]
    private async Task ConfirmGuidedRepairAsync()
    {
        if (IsBusy || _pendingGuidedSymptom is null || !EnsureReadyForUserWork()) return;
        if (!GuidedRepairRiskAccepted)
        {
            StatusText = _text.Get("GuidedRepair_NeedConsent");
            PushToast(
                _text.Get("GuidedRepair_ToastTitle"),
                _text.Get("GuidedRepair_NeedConsent"),
                "alert.svg",
                "warning");
            return;
        }

        var item = _pendingGuidedSymptom;
        ShowGuidedRepairConfirm = false;
        GuidedRepairRiskAccepted = false;
        _pendingGuidedSymptom = null;

        var module = _modules.FirstOrDefault(candidate =>
            candidate.Info.Id.Equals(item.Playbook.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (module is null)
        {
            StatusText = _text.Get("Status_Failed", item.ModuleName);
            return;
        }

        var fixes = ResolvePlaybookFixes(module, item.Playbook);
        if (fixes.Count == 0)
        {
            StatusText = _text.Get("GuidedRepair_NoFixes");
            return;
        }

        BeginOperation(
            _text.Get("GuidedRepair_Applying", item.Title),
            icon: item.ModuleIcon,
            phase: _text.Get("Operation_PhaseRepair"));
        UpdateOperation(
            _text.Get("GuidedRepair_Applying", item.Title),
            item.SideEffects,
            "alert.svg");
        AddOperationFeed(_text.Get("GuidedRepair_WarningShort"), "alert.svg", "warning");
        AddOperationFeed(item.SideEffects, "info.svg", "info");
        PushToast(
            _text.Get("Toast_RepairStarted"),
            _text.Get("GuidedRepair_Applying", item.Title),
            "wrench.svg",
            "info");

        try
        {
            _currentSession ??= await _sessions.CreateAsync(DryRun, _operationCts!.Token).ConfigureAwait(true);
            var sessionDryRun = _currentSession.DryRun;
            var maxTier = fixes.Max(fix => fix.Tier);

            if (!sessionDryRun && !HasUsableRestorePointGate())
            {
                UpdateOperation(
                    _text.Get("Status_RestorePoint"),
                    _text.Get("Operation_PreparingRestorePoint"),
                    "recovery.svg");
                var ensured = await EnsureSessionRestorePointAsync(maxTier, _operationCts!.Token)
                    .ConfigureAwait(true);
                if (!ensured)
                {
                    StatusText = _text.Get("Status_Cancelled");
                    PushToast(_text.Get("Toast_Cancelled"), _text.Get("Status_Cancelled"), "alert.svg", "warning");
                    return;
                }
            }

            // Keep side-effect reminder visible while changes run.
            OperationDetail = item.SideEffects;
            AddOperationFeed(_text.Get("GuidedRepair_RunningEffectsReminder"), "alert.svg", "warning");

            var appliedKeys = _currentSession.Actions
                .Where(action => action.Applied && !action.Undone)
                .Select(action => $"{action.FindingCheckId}\0{action.FixId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var requests = new List<FixRequest>();
            var syntheticFindings = new List<Finding>();
            foreach (var fix in fixes)
            {
                var checkId = $"symptom.{item.Playbook.Id}.{fix.Id}";
                if (appliedKeys.Contains($"{checkId}\0{fix.Id}")) continue;

                var finding = new Finding
                {
                    CheckId = checkId,
                    ModuleId = module.Info.Id,
                    Severity = Severity.Warning,
                    MessageKey = "Finding_GuidedSymptom",
                    MessageArguments = [item.Title, _text.Get(fix.TitleKey)],
                    RecommendedFixIds = [fix.Id],
                    TechnicalDetail = item.SideEffects
                };
                syntheticFindings.Add(finding);
                requests.Add(CreateFixRequest(finding, fix));
                appliedKeys.Add($"{checkId}\0{fix.Id}");
            }

            if (requests.Count == 0)
            {
                StatusText = _text.Get("Status_NoFixAtTier");
                return;
            }

            foreach (var finding in syntheticFindings)
            {
                Findings.Add(new FindingViewModel(
                    finding,
                    _text.Get(module.Info.NameKey),
                    module.Fixes,
                    _text));
                _currentSession.Findings.Add(ToSessionFinding(finding));
            }

            var progress = new Progress<FixProgress>(value =>
            {
                var fixTitle = _text.Get(value.TitleKey);
                var stage = _text.Get(value.StageKey);
                ActiveOperation = $"{fixTitle} · {stage}";
                OperationDetail = item.SideEffects + Environment.NewLine +
                                  _text.Get("Operation_RepairStep", fixTitle, stage, value.Completed, value.Total);
                OperationIcon = item.ModuleIcon;
                ReportFixPipelineProgress(value);
                AddOperationFeed($"{fixTitle} — {stage}", "wrench.svg", value.Success is false ? "warning" : "info");
            });

            var context = new FixContext
            {
                Commands = _commands,
                Backups = _backups,
                Console = _console,
                Text = _text,
                Session = _currentSession,
                DryRun = sessionDryRun,
                ForceModeConfirmed = false,
                RestorePointAvailable = sessionDryRun || HasUsableRestorePointGate(),
                AllowBackuplessAggressive = false,
                Thresholds = LoadThresholds()
            };
            await _fixes.RunAsync(requests, context, progress, _operationCts!.Token).ConfigureAwait(true);

            foreach (var findingVm in Findings)
            {
                findingVm.Refresh();
                var stored = _currentSession.Findings.FirstOrDefault(entry =>
                    entry.CheckId.Equals(findingVm.Model.CheckId, StringComparison.OrdinalIgnoreCase));
                if (stored is not null) stored.Status = findingVm.Model.Status;
            }

            await _sessions.SaveAsync(_currentSession, _operationCts.Token).ConfigureAwait(true);
            ReportPath = await _reports.CreateAsync(_currentSession, _text, _operationCts.Token).ConfigureAwait(true);

            var guided = Findings
                .Where(finding => syntheticFindings.Any(source =>
                    source.CheckId.Equals(finding.Model.CheckId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var resolved = guided.Count(finding => finding.Model.Status == FindingStatus.Resolved);
            var unresolved = guided.Length - resolved;
            PublishVerificationSummary(resolved, unresolved, sessionDryRun, _currentSession.PendingVerify);
            CurrentPage = 1;
            StatusText = sessionDryRun
                ? _text.Get("Status_PreviewComplete", guided.Length)
                : _text.Get("Status_RepairComplete", resolved, unresolved);
            PushToast(
                _text.Get("Toast_RepairStarted"),
                StatusText,
                resolved > 0 ? "check.svg" : "alert.svg",
                resolved > 0 ? "success" : "warning");
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
            PushToast(_text.Get("Toast_Cancelled"), StatusText, "alert.svg", "warning");
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
            PushToast(_text.Get("Toast_Failed"), ex.Message, "error.svg", "error");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ScanAsync(
        IReadOnlyList<IModuleDefinition> modules,
        bool quickOnly)
    {
        if (IsBusy || !EnsureReadyForUserWork()) return;

        BeginOperation(_text.Get("Status_Scanning"), icon: "search.svg", phase: _text.Get("Operation_PhaseScan"));
        ShowOperationSummary = false;
        OperationPassedCount = 0;
        OperationFindingCount = 0;
        OperationCommonFindingCount = 0;
        OperationActionableFindingCount = 0;
        PushToast(_text.Get("Toast_ScanStarted"), _text.Get("Status_Scanning"), "search.svg", "info");
        try
        {
            _currentSession = await _sessions.CreateAsync(DryRun, _operationCts!.Token).ConfigureAwait(true);
            // Scans are read-only diagnostics — never block on System Restore.
            ResetRestorePointState();
            ReportPath = null;
            Findings.Clear();
            ShowEscalation = false;
            ShowHandoff = false;
            ShowVerificationSummary = false;
            CurrentTier = RiskTier.Safe;
            RiskAccepted = false;

            var progress = new Progress<DiagnosticProgress>(value =>
            {
                var checkTitle = _text.Get(value.TitleKey);
                var moduleName = _modules.FirstOrDefault(module =>
                    module.Info.Id.Equals(value.ModuleId, StringComparison.OrdinalIgnoreCase)) is { } module
                    ? _text.Get(module.Info.NameKey)
                    : value.ModuleId;
                ActiveOperation = $"{checkTitle}  ·  {value.Completed}/{value.Total}";
                OperationDetail = _text.Get("Operation_ScanStep", moduleName, checkTitle, value.Completed, value.Total);
                OperationIcon = ResolveModuleIcon(value.ModuleId);
                ReportScanProgress(
                    value.Completed,
                    value.Total,
                    $"{checkTitle} · {value.Completed}/{value.Total}");
                if (value.Passed is true)
                {
                    OperationPassedCount++;
                    AddOperationFeed(_text.Get("Operation_FeedCheckPassed", checkTitle), "check.svg", "success");
                }
                else if (value.Passed is false)
                {
                    OperationFindingCount++;
                    AddOperationFeed(_text.Get("Operation_FeedCheckFinding", checkTitle), "alert.svg", "warning");
                }
                else
                {
                    AddOperationFeed(_text.Get("Operation_FeedCheckRunning", checkTitle), OperationIcon, "info");
                }

                OperationSummaryHeadline = _text.Get(
                    "Operation_LiveSummary",
                    OperationPassedCount,
                    OperationFindingCount);
            });
            var context = new DiagnosticContext
            {
                Commands = _commands,
                Text = _text,
                Thresholds = LoadThresholds(),
                SessionDirectory = _currentSession.DirectoryPath
            };
            var findings = await _diagnostics.RunAsync(
                modules,
                context,
                quickOnly,
                progress,
                _operationCts.Token).ConfigureAwait(true);

            var moduleMap = _modules.ToDictionary(module => module.Info.Id, module => module);
            foreach (var finding in findings)
            {
                var module = moduleMap[finding.ModuleId];
                Findings.Add(new FindingViewModel(finding, _text.Get(module.Info.NameKey), module.Fixes, _text));
                _currentSession.Findings.Add(ToSessionFinding(finding));
            }
            _currentSession.Status = SessionStatus.Completed;
            _currentSession.CompletedAt = DateTimeOffset.Now;
            await _sessions.SaveAsync(_currentSession, _operationCts.Token).ConfigureAwait(true);
            StatusText = _text.Get("Status_ScanComplete", Findings.Count);
            PublishScanSummary(findings.Count, OperationPassedCount);
            PushToast(
                _text.Get("Toast_ScanComplete"),
                OperationSummaryHeadline,
                Findings.Count > 0 ? "alert.svg" : "check.svg",
                Findings.Count > 0 ? "warning" : "success");
            CurrentPage = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
            PushToast(_text.Get("Toast_Cancelled"), _text.Get("Status_Cancelled"), "alert.svg", "warning");
            if (_currentSession is not null)
            {
                _currentSession.Status = SessionStatus.Cancelled;
                await _sessions.SaveAsync(_currentSession, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
            PushToast(_text.Get("Toast_Failed"), ex.Message, "error.svg", "error");
            if (_currentSession is not null)
            {
                _currentSession.Status = SessionStatus.Failed;
                await _sessions.SaveAsync(_currentSession, CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task RepairSelectedAsync()
    {
        if (IsBusy || _currentSession is null || !EnsureReadyForUserWork()) return;

        var sessionDryRun = _currentSession.DryRun;
        var selected = Findings.Where(finding => finding.IsSelected && finding.Model.Status != FindingStatus.Resolved).ToArray();
        if (selected.Length == 0)
        {
            StatusText = _text.Get("Status_NoSelection");
            return;
        }

        if (CurrentTier == RiskTier.Aggressive && !RiskAccepted)
        {
            StatusText = _text.Get("Dialog_ForceNeedsConsent");
            return;
        }

        BeginOperation(_text.Get("Status_RepairingNow"), icon: "wrench.svg", phase: _text.Get("Operation_PhaseRepair"));
        PushToast(_text.Get("Toast_RepairStarted"), _text.Get("Status_RepairingNow"), "wrench.svg", "info");
        ShowHandoff = false;
        ShowVerificationSummary = false;
        try
        {
            if (!sessionDryRun &&
                !HasUsableRestorePointGate() &&
                (!_restorePointAttempted || CurrentTier == RiskTier.Aggressive && !_restorePointCreated))
            {
                UpdateOperation(
                    _text.Get("Status_RestorePoint"),
                    _text.Get("Operation_PreparingRestorePoint"),
                    "recovery.svg");
                var ensured = await EnsureSessionRestorePointAsync(CurrentTier, _operationCts!.Token)
                    .ConfigureAwait(true);
                if (!ensured)
                {
                    StatusText = _text.Get("Status_Cancelled");
                    PushToast(_text.Get("Toast_Cancelled"), _text.Get("Status_Cancelled"), "alert.svg", "warning");
                    return;
                }
            }

            if (CurrentTier == RiskTier.Aggressive && !sessionDryRun && !HasUsableRestorePointGate())
            {
                StatusText = _text.Get("Status_Cancelled");
                return;
            }

            var allowBackuplessAggressive = false;
            if (CurrentTier == RiskTier.Aggressive && !sessionDryRun && RiskAccepted)
            {
                var backuplessConsent = MessageBox.Show(
                    _text.Get("Dialog_BackuplessAggressive"),
                    _text.Get("Dialog_BackuplessAggressive_Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                allowBackuplessAggressive = backuplessConsent == MessageBoxResult.Yes;
            }

            var appliedKeys = _currentSession.Actions
                .Where(action => action.Applied && !action.Undone)
                .Select(action => $"{action.FindingCheckId}\0{action.FixId}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requests = new List<FixRequest>();
            foreach (var item in selected)
            {
                var candidates = item.FixChoices.Where(choice =>
                    choice.IsSelected &&
                    choice.IsAvailable &&
                    choice.Fix.Tier == CurrentTier &&
                    !appliedKeys.Contains($"{item.Model.CheckId}\0{choice.Fix.Id}"));
                foreach (var choice in candidates)
                {
                    requests.Add(CreateFixRequest(item.Model, choice.Fix));
                    appliedKeys.Add($"{item.Model.CheckId}\0{choice.Fix.Id}");
                }
            }

            // Force mode: queue every Aggressive fix for modules of unresolved selected findings.
            if (CurrentTier == RiskTier.Aggressive && RiskAccepted)
            {
                foreach (var request in BuildAllAggressiveModuleRequests(selected, appliedKeys))
                {
                    requests.Add(request);
                    appliedKeys.Add($"{request.Finding.CheckId}\0{request.Fix.Id}");
                }
            }

            if (requests.Count == 0)
            {
                var nextTier = RepairTierPlanner.NextAvailableTier(CurrentTier, selected
                    .SelectMany(item => item.FixChoices
                        .Where(choice => choice.IsSelected &&
                                         choice.IsAvailable &&
                                         !appliedKeys.Contains($"{item.Model.CheckId}\0{choice.Fix.Id}") &&
                                         (int)choice.Fix.Tier > (int)CurrentTier)
                        .Select(choice => choice.Fix.Tier)));
                if (nextTier is not null)
                {
                    CurrentTier = nextTier.Value;
                    ShowEscalation = true;
                    StatusText = EscalationText;
                    return;
                }

                ShowEscalation = false;
                ShowHandoff = true;
                StatusText = HandoffTitle;
                return;
            }

            var progress = new Progress<FixProgress>(value =>
            {
                var fixTitle = _text.Get(value.TitleKey);
                var stage = _text.Get(value.StageKey);
                ActiveOperation = $"{fixTitle} · {stage}";
                OperationDetail = _text.Get("Operation_RepairStep", fixTitle, stage, value.Completed, value.Total);
                OperationIcon = "wrench.svg";
                ReportFixPipelineProgress(value);
                AddOperationFeed($"{fixTitle} — {stage}", "wrench.svg", value.Success is false ? "warning" : "info");
            });
            var context = new FixContext
            {
                Commands = _commands,
                Backups = _backups,
                Console = _console,
                Text = _text,
                Session = _currentSession,
                DryRun = sessionDryRun,
                ForceModeConfirmed = RiskAccepted,
                RestorePointAvailable = sessionDryRun || HasUsableRestorePointGate(),
                AllowBackuplessAggressive = allowBackuplessAggressive,
                Thresholds = LoadThresholds()
            };
            await _fixes.RunAsync(requests, context, progress, _operationCts!.Token).ConfigureAwait(true);

            foreach (var finding in Findings)
            {
                finding.Refresh();
                var stored = _currentSession.Findings.FirstOrDefault(item => item.CheckId == finding.Model.CheckId);
                if (stored is not null) stored.Status = finding.Model.Status;
            }
            await _sessions.SaveAsync(_currentSession, _operationCts.Token).ConfigureAwait(true);
            ReportPath = await _reports.CreateAsync(_currentSession, _text, _operationCts.Token).ConfigureAwait(true);

            var resolved = selected.Count(item => item.Model.Status == FindingStatus.Resolved);
            var unresolved = selected.Length - resolved;
            PublishVerificationSummary(resolved, unresolved, sessionDryRun, _currentSession.PendingVerify);
            if (sessionDryRun)
            {
                StatusText = _text.Get("Status_PreviewComplete", selected.Length);
                ShowEscalation = false;
                ShowHandoff = false;
                return;
            }
            if (_currentSession.PendingVerify)
            {
                StatusText = _text.Get("Status_PendingRebootSummary");
                ShowEscalation = false;
                ShowHandoff = false;
                return;
            }

            StatusText = _text.Get("Status_RepairComplete", resolved, unresolved);
            var nextAvailableTier = RepairTierPlanner.NextAvailableTier(CurrentTier, selected
                .SelectMany(item => item.FixChoices
                    .Where(choice => choice.IsSelected && choice.IsAvailable && (int)choice.Fix.Tier > (int)CurrentTier &&
                                     !_currentSession.Actions.Any(action =>
                                         action.Applied && !action.Undone &&
                                         action.FindingCheckId.Equals(item.Model.CheckId, StringComparison.OrdinalIgnoreCase) &&
                                         action.FixId.Equals(choice.Fix.Id, StringComparison.OrdinalIgnoreCase)))
                    .Select(choice => choice.Fix.Tier)));
            if (unresolved > 0 && nextAvailableTier is not null)
            {
                CurrentTier = nextAvailableTier.Value;
                ShowEscalation = true;
                ShowHandoff = false;
            }
            else if (unresolved > 0)
            {
                ShowEscalation = false;
                ShowHandoff = true;
                StatusText = HandoffTitle;
            }
            else
            {
                ShowEscalation = false;
                ShowHandoff = false;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
        }
        finally
        {
            try
            {
                await LoadRecoverySessionsAsync().ConfigureAwait(true);
            }
            catch
            {
                // Recovery list refresh must not hide the original repair error.
            }

            if (RecoveryRequired)
            {
                CurrentPage = 3;
                if (!StatusText.Equals(_text.Get("Status_Cancelled"), StringComparison.Ordinal))
                {
                    StatusText = RecoveryBannerText;
                }
            }

            EndOperation();
        }
    }

    private async Task RunLiveTestAsync(LiveTestItemViewModel item)
    {
        if (IsBusy || !EnsureReadyForUserWork()) return;
        BeginOperation(item.Title);
        var operationToken = _operationCts!.Token;
        item.IsRunning = true;
        item.Progress = 0;
        item.ResultSeverity = null;
        TestOutput.Clear();
        foreach (var previous in LiveMetricRows.Where(row => row.TestId == item.Test.Id).ToArray())
        {
            LiveMetricRows.Remove(previous);
        }
        try
        {
            _currentSession ??= await _sessions.CreateAsync(DryRun, operationToken).ConfigureAwait(true);
            var context = new DiagnosticContext
            {
                Commands = _commands,
                Text = _text,
                Thresholds = LoadThresholds(),
                SessionDirectory = _currentSession.DirectoryPath
            };
            var channel = Channel.CreateBounded<TestProgress>(new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            var producer = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    await foreach (var value in item.Test.RunAsync(context, operationToken).ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(value, operationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    channel.Writer.TryComplete(failure);
                }
            }, operationToken);

            await foreach (var value in channel.Reader.ReadAllAsync(operationToken).ConfigureAwait(true))
            {
                item.Progress = value.Progress * 100;
                item.LastResult = BoundStoredDetail($"{_text.Get(value.StageKey)} · {value.Detail}");
                var resultSeverity = ClassifyTestProgress(value);
                if (item.ResultSeverity is null || (int)resultSeverity > (int)item.ResultSeverity.Value)
                {
                    item.ResultSeverity = resultSeverity;
                }
                var metricRow = CreateMetricRow(item, value, resultSeverity);
                if (metricRow is not null)
                {
                    LiveMetricRows.Add(metricRow);
                    while (LiveMetricRows.Count > 100) LiveMetricRows.RemoveAt(0);
                }
                TestOutput.Add(item.LastResult);
                while (TestOutput.Count > 500) TestOutput.RemoveAt(0);
                ProgressValue = item.Progress;
                ActiveOperation = item.LastResult;

                var userRejected = false;
                if (value.RequiresUserAnswer)
                {
                    var heard = MessageBox.Show(
                        _text.Get("Dialog_LiveTestConfirm"),
                        item.Title,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    userRejected = heard == MessageBoxResult.No;
                }

                var measuredFailure = value.StageKey is "TestStage_NoSignal" or "TestStage_Failed";
                if (item.Test.ModuleId.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                    (userRejected || measuredFailure) &&
                    Findings.All(finding => !finding.Model.CheckId.Equals(item.Test.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var messageKey = item.Test.Id.Contains("microphone", StringComparison.OrdinalIgnoreCase)
                        ? "Finding_Audio_MicrophoneLiveFailed"
                        : item.Test.Id.Contains("stability", StringComparison.OrdinalIgnoreCase)
                            ? "Finding_Audio_StabilityLiveFailed"
                            : "Finding_Audio_SpeakerLiveFailed";
                    string[] fixes = item.Test.Id.Contains("stability", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "audio.disable-enhancements", "audio.format-reset", "audio.restart-services" }
                        : new[] { "audio.restart-services", "audio.unmute", "audio.set-default" };
                    var finding = new Finding
                    {
                        CheckId = item.Test.Id,
                        ModuleId = item.Test.ModuleId,
                        Severity = Severity.Warning,
                        MessageKey = messageKey,
                        TechnicalDetail = value.Detail,
                        RecommendedFixIds = fixes
                    };
                    var module = _modules.First(module => module.Info.Id == finding.ModuleId);
                    Findings.Add(new FindingViewModel(finding, _text.Get(module.Info.NameKey), module.Fixes, _text));
                    _currentSession.Findings.Add(ToSessionFinding(finding));
                }
            }
            await producer.ConfigureAwait(true);
            if (_currentSession.Status == SessionStatus.Created)
            {
                _currentSession.Status = SessionStatus.Completed;
                _currentSession.CompletedAt = DateTimeOffset.Now;
            }
            await _sessions.SaveAsync(_currentSession, operationToken).ConfigureAwait(true);
            StatusText = item.LastResult;
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
        }
        catch (Exception ex)
        {
            item.LastResult = _text.Get("Status_Failed", ex.Message);
            StatusText = item.LastResult;
        }
        finally
        {
            item.IsRunning = false;
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task CreateSupportPackageAsync()
    {
        if (_currentSession is null || IsBusy || !_startupComplete) return;
        var consent = MessageBox.Show(
            _text.Get("Dialog_SupportPrivacy"),
            _text.Get("Dialog_SupportPrivacy_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        BeginOperation(_text.Get("Action_CreateSupportPackage"));
        try
        {
            var zip = await _support.CreateAsync(_currentSession, ReportPath, _operationCts!.Token).ConfigureAwait(true);
            StatusText = _text.Get("Status_SupportCreated", zip);
            OpenPath(zip);
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (HasReport) OpenPath(ReportPath!);
    }

    private async Task LoadRecoverySessionsAsync()
    {
        var sessions = await _sessions.ListAsync(CancellationToken.None).ConfigureAwait(true);
        RecoveryRequired = sessions.Any(SessionRecoveryGates.SessionRequiresRecovery);
        var fixTitles = _modules
            .SelectMany(module => module.Fixes)
            .GroupBy(fix => fix.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => _text.Get(group.First().TitleKey), StringComparer.OrdinalIgnoreCase);
        RecoverySessions.Clear();
        foreach (var session in sessions
                     .Where(session => session.Actions.Count > 0)
                     .OrderByDescending(SessionRecoveryGates.SessionRequiresRecovery)
                     .ThenByDescending(session => session.PendingVerify)
                     .ThenByDescending(session => session.StartedAt)
                     .Take(MaximumRecoverySessionsShown))
        {
            RecoverySessions.Add(new RecoverySessionViewModel(
                session,
                _text,
                fixTitles,
                UndoSessionAsync,
                UndoActionAsync,
                DismissSessionRecoveryAsync,
                DismissActionRecoveryAsync,
                OpenPath));
        }
    }

    private bool EnsureReadyForUserWork()
    {
        if (!_startupComplete)
        {
            StatusText = _text.Get("Status_Starting");
            return false;
        }

        if (RecoveryRequired)
        {
            CurrentPage = 3;
            StatusText = RecoveryBannerText;
            return false;
        }

        return true;
    }

    private bool HasUsableRestorePointGate() =>
        _restorePointCreated || _restorePointBypassAccepted;

    private void ResetRestorePointState()
    {
        _restorePointAttempted = false;
        _restorePointCreated = false;
        _restorePointBypassAccepted = false;
        CanSkipRestorePoint = false;
        try
        {
            _restorePointCts?.Cancel();
        }
        catch
        {
            // Ignore dispose races while resetting session state.
        }

        _restorePointCts?.Dispose();
        _restorePointCts = null;
    }

    /// <summary>
    /// Offers create / skip-with-warning, then creates a restore point with a short timeout.
    /// Returns false when the user aborts the repair path.
    /// </summary>
    private async Task<bool> EnsureSessionRestorePointAsync(RiskTier tier, CancellationToken ct)
    {
        if (_currentSession is null || _currentSession.DryRun) return true;
        if (_restorePointCreated || _restorePointBypassAccepted) return true;

        StatusText = _text.Get("Status_RestorePoint");
        UpdateOperation(
            _text.Get("Status_RestorePoint"),
            _text.Get("Operation_PreparingRestorePoint"),
            "recovery.svg");

        // Create / Skip / Cancel before starting the potentially long Windows call.
        var offer = MessageBox.Show(
            _text.Get("Dialog_RestorePointOffer"),
            _text.Get("Dialog_RestorePointOffer_Title"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (offer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (offer == MessageBoxResult.No)
        {
            return AcceptRestorePointBypass(tier, skippedUpFront: true);
        }

        _restorePointAttempted = true;
        CanSkipRestorePoint = true;
        _restorePointCts?.Dispose();
        _restorePointCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var restoreCt = _restorePointCts.Token;

        try
        {
            AddOperationFeed(_text.Get("Operation_RestorePointChecking"), "recovery.svg", "info");
            var protectionAvailable = await _restorePoints.IsSystemProtectionAvailableAsync(restoreCt)
                .ConfigureAwait(true);
            if (!protectionAvailable)
            {
                AddOperationFeed(_text.Get("Dialog_SystemProtectionOff"), "alert.svg", "warning");
                MessageBox.Show(
                    _text.Get("Dialog_SystemProtectionOff"),
                    _text.Get("Dialog_SystemProtectionOff_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            UpdateOperation(
                _text.Get("Status_RestorePoint"),
                _text.Get("Operation_RestorePointWorking"),
                "recovery.svg");

            // Heartbeat so the overlay does not look frozen while Checkpoint-Computer runs silently.
            var createTask = _restorePoints.CreateAsync($"CaYaFix {_currentSession.Id}", restoreCt);
            var elapsedSeconds = 0;
            while (!createTask.IsCompleted && !restoreCt.IsCancellationRequested)
            {
                var delayTask = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
                var finished = await Task.WhenAny(createTask, delayTask).ConfigureAwait(true);
                if (finished == createTask)
                {
                    break;
                }

                elapsedSeconds += 5;
                OperationDetail = _text.Get("Operation_RestorePointWaiting", elapsedSeconds);
                ActiveOperation = OperationDetail;
                StatusText = OperationDetail;
            }

            if (restoreCt.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Drain the cancelled create task so exceptions are not unobserved.
                try
                {
                    await createTask.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the user skips mid-create.
                }

                // User pressed Skip during create.
                if (_restorePointBypassAccepted)
                {
                    AddOperationFeed(_text.Get("Operation_RestorePointSkipped"), "alert.svg", "warning");
                    _currentSession.RestorePointCreated = false;
                    await _sessions.SaveAsync(_currentSession, CancellationToken.None).ConfigureAwait(true);
                    return true;
                }

                _restorePointCreated = false;
            }
            else
            {
                var result = await createTask.ConfigureAwait(true);
                _restorePointCreated = result.IsUsable;
                if (_restorePointCreated)
                {
                    AddOperationFeed(
                        result.Status == RestorePointCreateStatus.AlreadyPresent
                            ? _text.Get("Operation_RestorePointAlreadyPresent")
                            : _text.Get("Operation_RestorePointCreated"),
                        "check.svg",
                        "success");
                }
                else
                {
                    AddOperationFeed(
                        _text.Get("Operation_RestorePointFailedDetail", result.Detail),
                        "alert.svg",
                        "warning");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (_restorePointBypassAccepted)
            {
                AddOperationFeed(_text.Get("Operation_RestorePointSkipped"), "alert.svg", "warning");
                _currentSession.RestorePointCreated = false;
                await _sessions.SaveAsync(_currentSession, CancellationToken.None).ConfigureAwait(true);
                return true;
            }

            _restorePointCreated = false;
        }
        catch (OperationCanceledException)
        {
            _restorePointAttempted = false;
            throw;
        }
        catch (Exception ex)
        {
            _restorePointCreated = false;
            AddOperationFeed(
                _text.Get("Operation_RestorePointFailedDetail", ex.Message),
                "error.svg",
                "error");
        }
        finally
        {
            CanSkipRestorePoint = false;
            _restorePointCts?.Dispose();
            _restorePointCts = null;
        }

        if (_restorePointCreated)
        {
            _currentSession.RestorePointCreated = true;
            await _sessions.SaveAsync(_currentSession, ct).ConfigureAwait(true);
            return true;
        }

        // Create failed / timed out / skipped without prior bypass — ask to continue with warning.
        return AcceptRestorePointBypass(tier, skippedUpFront: false);
    }

    private bool AcceptRestorePointBypass(RiskTier tier, bool skippedUpFront)
    {
        _restorePointAttempted = true;
        _restorePointCreated = false;

        if (tier == RiskTier.Aggressive)
        {
            var forceProceed = MessageBox.Show(
                _text.Get(skippedUpFront
                    ? "Dialog_SkipRestoreAggressive"
                    : "Dialog_ForceNeedsRestore"),
                _text.Get("Dialog_RestorePointFailed_Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (forceProceed != MessageBoxResult.Yes)
            {
                return false;
            }

            _restorePointBypassAccepted = true;
            AddOperationFeed(_text.Get("Operation_RestorePointSkipped"), "alert.svg", "warning");
            if (_currentSession is not null)
            {
                _currentSession.RestorePointCreated = false;
                _ = _sessions.SaveAsync(_currentSession, CancellationToken.None);
            }

            return true;
        }

        var proceed = MessageBox.Show(
            _text.Get(skippedUpFront
                ? "Dialog_SkipRestorePoint"
                : "Dialog_RestorePointFailed"),
            _text.Get(skippedUpFront
                ? "Dialog_SkipRestorePoint_Title"
                : "Dialog_RestorePointFailed_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (proceed != MessageBoxResult.Yes)
        {
            return false;
        }

        _restorePointBypassAccepted = true;
        AddOperationFeed(_text.Get("Operation_RestorePointSkipped"), "alert.svg", "warning");
        if (_currentSession is not null)
        {
            _currentSession.RestorePointCreated = false;
            _ = _sessions.SaveAsync(_currentSession, CancellationToken.None);
        }

        return true;
    }

    [RelayCommand]
    private void SkipRestorePoint()
    {
        if (!CanSkipRestorePoint || _restorePointCts is null)
        {
            return;
        }

        var aggressive = CurrentTier == RiskTier.Aggressive;
        var confirm = MessageBox.Show(
            _text.Get(aggressive ? "Dialog_SkipRestoreAggressive" : "Dialog_SkipRestorePoint"),
            _text.Get(aggressive ? "Dialog_RestorePointFailed_Title" : "Dialog_SkipRestorePoint_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _restorePointBypassAccepted = true;
        try
        {
            _restorePointCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Create already finished.
        }
    }

    private FixRequest CreateFixRequest(Finding finding, FixAction fix)
    {
        var prefix = $"{fix.Id}.";
        var requestParameters = finding.RepairParameters
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var diagnosticCheck = _modules
            .First(module => module.Info.Id.Equals(finding.ModuleId, StringComparison.OrdinalIgnoreCase))
            .Checks
            .FirstOrDefault(check =>
                check.Id.Equals(finding.CheckId, StringComparison.OrdinalIgnoreCase) &&
                check.SupportsPostRepairVerification);
        return new FixRequest(finding, fix, requestParameters, diagnosticCheck);
    }

    private IEnumerable<FixRequest> BuildAllAggressiveModuleRequests(
        IReadOnlyList<FindingViewModel> selected,
        HashSet<string> appliedKeys)
    {
        var moduleIds = selected
            .Select(item => item.Model.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var moduleId in moduleIds)
        {
            var module = _modules.FirstOrDefault(item =>
                item.Info.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
            if (module is null) continue;

            var anchorFinding = selected
                .Select(item => item.Model)
                .FirstOrDefault(finding =>
                    finding.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase) &&
                    finding.Status != FindingStatus.Resolved);
            if (anchorFinding is null) continue;

            foreach (var fix in module.Fixes.Where(candidate => candidate.Tier == RiskTier.Aggressive))
            {
                var key = $"{anchorFinding.CheckId}\0{fix.Id}";
                if (appliedKeys.Contains(key)) continue;
                if (fix.RequiresTargetParameter &&
                    !anchorFinding.RepairParameters.Keys.Any(parameter =>
                        parameter.StartsWith($"{fix.Id}.", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(anchorFinding.RepairParameters[parameter])))
                {
                    // Fall back to any selected finding in the module that carries a target.
                    var targeted = selected
                        .Select(item => item.Model)
                        .FirstOrDefault(finding =>
                            finding.ModuleId.Equals(moduleId, StringComparison.OrdinalIgnoreCase) &&
                            finding.RepairParameters.Keys.Any(parameter =>
                                parameter.Equals($"{fix.Id}.target", StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrWhiteSpace(finding.RepairParameters[parameter])));
                    if (targeted is null) continue;
                    yield return CreateFixRequest(targeted, fix);
                    continue;
                }

                yield return CreateFixRequest(anchorFinding, fix);
            }
        }
    }

    private void PublishVerificationSummary(int resolved, int unresolved, bool dryRun, bool pendingReboot)
    {
        ShowVerificationSummary = true;
        VerificationSummaryTitle = _text.Get("Verification_Title");
        if (dryRun)
        {
            VerificationSummaryText = _text.Get("Verification_Preview", resolved + unresolved);
            return;
        }

        if (pendingReboot)
        {
            VerificationSummaryText = _text.Get("Verification_PendingReboot", resolved, unresolved);
            return;
        }

        var lines = Findings
            .Where(finding => finding.IsSelected || finding.Model.Status is FindingStatus.Resolved or FindingStatus.Unresolved)
            .Select(finding => $"{finding.Message} — {_text.Get($"Status_{finding.Model.Status}")}")
            .Take(24)
            .ToArray();
        VerificationSummaryText = _text.Get("Verification_Summary", resolved, unresolved) +
                                  (lines.Length == 0
                                      ? string.Empty
                                      : Environment.NewLine + string.Join(Environment.NewLine, lines));
    }

    private async Task RunExpertFixAsync(ExpertFixViewModel item)
    {
        // Manual tools catalog is available without Expert Mode; Aggressive still needs risk acceptance.
        if (IsBusy || !EnsureReadyForUserWork()) return;

        if (item.Fix.Tier == RiskTier.Aggressive && !RiskAccepted)
        {
            CurrentPage = 1;
            CurrentTier = RiskTier.Aggressive;
            ShowEscalation = true;
            IsForceStage = true;
            ExpertMode = true;
            StatusText = _text.Get("Dialog_ForceNeedsConsent");
            PushToast(
                _text.Get("Dialog_ForceNeedsConsent"),
                _text.Get("Settings_ManualToolsAggressiveHint"),
                "alert.svg",
                "warning");
            return;
        }

        Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);
        if (item.Fix.RequiresTargetParameter)
        {
            var targetKey = $"{item.Fix.Id}.target";
            var source = Findings
                .Select(finding => finding.Model)
                .SelectMany(finding => finding.RepairParameters)
                .FirstOrDefault(pair =>
                    pair.Key.Equals(targetKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value));
            if (string.IsNullOrWhiteSpace(source.Value))
            {
                StatusText = _text.Get("Status_ExpertTargetMissing", item.Title);
                return;
            }

            parameters[targetKey] = source.Value;
        }

        var consent = MessageBox.Show(
            _text.Get("Dialog_ExpertRunFix", item.Title, _text.Get($"Tier_{item.Tier}")),
            _text.Get("Dialog_ExpertRunFix_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        BeginOperation(_text.Get("Status_RepairingNow"));
        ShowVerificationSummary = false;
        try
        {
            _currentSession ??= await _sessions.CreateAsync(DryRun, _operationCts!.Token).ConfigureAwait(true);
            if (!DryRun &&
                !HasUsableRestorePointGate() &&
                (!_restorePointAttempted || item.Fix.Tier == RiskTier.Aggressive && !_restorePointCreated))
            {
                UpdateOperation(
                    _text.Get("Status_RestorePoint"),
                    _text.Get("Operation_PreparingRestorePoint"),
                    "recovery.svg");
                var ensured = await EnsureSessionRestorePointAsync(item.Fix.Tier, _operationCts!.Token)
                    .ConfigureAwait(true);
                if (!ensured)
                {
                    StatusText = _text.Get("Status_Cancelled");
                    return;
                }
            }

            if (item.Fix.Tier == RiskTier.Aggressive && !DryRun && !HasUsableRestorePointGate())
            {
                StatusText = _text.Get("Status_Cancelled");
                return;
            }

            var allowBackupless = false;
            if (item.Fix.Tier == RiskTier.Aggressive && !DryRun && RiskAccepted)
            {
                var backuplessConsent = MessageBox.Show(
                    _text.Get("Dialog_BackuplessAggressive"),
                    _text.Get("Dialog_BackuplessAggressive_Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                allowBackupless = backuplessConsent == MessageBoxResult.Yes;
            }

            var finding = new Finding
            {
                CheckId = "expert.manual",
                ModuleId = item.Fix.ModuleId,
                Severity = Severity.Info,
                MessageKey = "Finding_ExpertManual",
                MessageArguments = [item.Title],
                RecommendedFixIds = [item.Fix.Id],
                RepairParameters = parameters
            };
            var request = new FixRequest(finding, item.Fix, parameters);
            var context = new FixContext
            {
                Commands = _commands,
                Backups = _backups,
                Console = _console,
                Text = _text,
                Session = _currentSession,
                DryRun = DryRun,
                ForceModeConfirmed = RiskAccepted || item.Fix.Tier != RiskTier.Aggressive,
                RestorePointAvailable = DryRun || HasUsableRestorePointGate(),
                AllowBackuplessAggressive = allowBackupless,
                Thresholds = LoadThresholds()
            };
            var results = await _fixes.RunAsync([request], context, null, _operationCts!.Token).ConfigureAwait(true);
            _currentSession.Findings.Add(ToSessionFinding(finding));
            await _sessions.SaveAsync(_currentSession, _operationCts.Token).ConfigureAwait(true);
            ReportPath = await _reports.CreateAsync(_currentSession, _text, _operationCts.Token).ConfigureAwait(true);
            var result = results.FirstOrDefault();
            StatusText = result is null
                ? _text.Get("Status_RepairComplete", 0, 1)
                : _text.Get(result.MessageKey);
            ShowVerificationSummary = true;
            VerificationSummaryTitle = _text.Get("Verification_Title");
            VerificationSummaryText = _text.Get(
                "Verification_ExpertResult",
                item.Title,
                result is null ? "—" : _text.Get(result.MessageKey));
            CurrentPage = 1;
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_Cancelled");
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
        }
        finally
        {
            try
            {
                await LoadRecoverySessionsAsync().ConfigureAwait(true);
            }
            catch
            {
                // Non-fatal.
            }

            if (RecoveryRequired)
            {
                CurrentPage = 3;
                StatusText = RecoveryBannerText;
            }

            EndOperation();
        }
    }

    private async Task<bool> RollbackPendingActionGroupAsync(
        IEnumerable<SessionActionRecord> actions,
        SessionManifest session)
    {
        var rolledBackAny = false;
        foreach (var action in actions.Reverse())
        {
            bool restored;
            try
            {
                restored = action.Backup is not null &&
                           await _backups.RestoreAsync(action.Backup, CancellationToken.None)
                               .ConfigureAwait(true);
            }
            catch
            {
                restored = false;
            }

            action.Undone = restored;
            action.ResultMessageKey = restored
                ? "FixResult_VerificationFailedRolledBack"
                : "FixResult_VerificationFailedRollbackFailed";
            rolledBackAny |= restored;
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(true);
        }

        return rolledBackAny;
    }

    private async Task UndoActionAsync(RecoveryActionViewModel item)
    {
        if (IsBusy || !item.IsUndoAvailable) return;
        var consent = MessageBox.Show(
            _text.Get("Dialog_RollbackAction"),
            _text.Get("Dialog_RollbackAction_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        BeginOperation(_text.Get("Action_UndoAction"), isRollback: true, icon: "recovery.svg", phase: _text.Get("Operation_PhaseRollback"));
        AddOperationFeed(_text.Get("Operation_FeedRollbackAction", item.Title), "recovery.svg", "warning");
        try
        {
            var success = await _sessions.UndoActionAsync(
                item.Session,
                item.ActionIndex,
                _backups,
                _operationCts!.Token).ConfigureAwait(true);
            StatusText = _text.Get(success ? "Status_RollbackComplete" : "Status_RollbackFailed");
            PushToast(
                success ? _text.Get("Toast_RollbackComplete") : _text.Get("Toast_RollbackFailed"),
                StatusText,
                success ? "check.svg" : "error.svg",
                success ? "success" : "error");
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_RollbackCancelled");
            MessageBox.Show(
                _text.Get("Dialog_RollbackCancelled"),
                _text.Get("Dialog_RollbackCancelled_Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
            PushToast(_text.Get("Toast_Failed"), ex.Message, "error.svg", "error");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task UndoSessionAsync(RecoverySessionViewModel item)
    {
        if (IsBusy || !item.IsUndoAvailable) return;
        var consent = MessageBox.Show(
            _text.Get("Dialog_Rollback"),
            _text.Get("Dialog_Rollback_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        BeginOperation(_text.Get("Action_UndoSession"), isRollback: true, icon: "recovery.svg", phase: _text.Get("Operation_PhaseRollback"));
        AddOperationFeed(_text.Get("Operation_FeedRollbackSession", item.Title), "recovery.svg", "warning");
        try
        {
            var success = await _sessions.UndoSessionAsync(item.Session, _backups, _operationCts!.Token).ConfigureAwait(true);
            StatusText = _text.Get(success ? "Status_RollbackComplete" : "Status_RollbackFailed");
            PushToast(
                success ? _text.Get("Toast_RollbackComplete") : _text.Get("Toast_RollbackFailed"),
                StatusText,
                success ? "check.svg" : "error.svg",
                success ? "success" : "error");
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = _text.Get("Status_RollbackCancelled");
            MessageBox.Show(
                _text.Get("Dialog_RollbackCancelled"),
                _text.Get("Dialog_RollbackCancelled_Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
            PushToast(_text.Get("Toast_Failed"), ex.Message, "error.svg", "error");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task DismissActionRecoveryAsync(RecoveryActionViewModel item)
    {
        if (IsBusy || !item.IsDismissAvailable) return;
        var consent = MessageBox.Show(
            _text.Get("Dialog_DismissRecoveryAction"),
            _text.Get("Dialog_DismissRecovery_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        try
        {
            var success = await _sessions.DismissActionAsync(
                item.Session,
                item.ActionIndex,
                CancellationToken.None).ConfigureAwait(true);
            StatusText = _text.Get(success ? "Status_RecoveryDismissed" : "Status_RecoveryDismissFailed");
            PushToast(
                success ? _text.Get("Toast_RecoveryDismissed") : _text.Get("Toast_Failed"),
                StatusText,
                success ? "check.svg" : "error.svg",
                success ? "warning" : "error");
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
        }
    }

    private async Task DismissSessionRecoveryAsync(RecoverySessionViewModel item)
    {
        if (IsBusy || !item.IsDismissAvailable) return;
        var consent = MessageBox.Show(
            _text.Get("Dialog_DismissRecoverySession"),
            _text.Get("Dialog_DismissRecovery_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (consent != MessageBoxResult.Yes) return;

        try
        {
            var success = await _sessions.DismissSessionRecoveryAsync(item.Session, CancellationToken.None)
                .ConfigureAwait(true);
            StatusText = _text.Get(success ? "Status_RecoveryDismissed" : "Status_RecoveryDismissFailed");
            PushToast(
                success ? _text.Get("Toast_RecoveryDismissed") : _text.Get("Toast_Failed"),
                StatusText,
                success ? "check.svg" : "error.svg",
                success ? "warning" : "error");
            await LoadRecoverySessionsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _text.Get("Status_Failed", ex.Message);
        }
    }

    private void BeginOperation(string title, bool isRollback = false, string icon = "search.svg", string? phase = null)
    {
        // Don't keep the home module overlay under scan/repair dialogs.
        if (HasSelectedModule)
        {
            CloseModulePanel();
        }

        if (ShowGuidedRepairConfirm)
        {
            CancelGuidedRepair();
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        CanSkipRestorePoint = false;
        _restorePointCts?.Dispose();
        _restorePointCts = null;
        _isRollbackOperation = isRollback;
        OperationFeed.Clear();
        IsBusy = true;
        _operationStartedAt = DateTimeOffset.Now;
        _progressFloor = 0;
        _progressCeiling = 100;
        _toolPercent = 0;
        _expectedToolMinutes = 0;
        _currentFixId = string.Empty;
        _currentStageKey = string.Empty;
        SetOperationProgress(0, forceEta: true);
        ProgressStatusText = title;
        ActiveOperation = title;
        StatusText = title;
        OperationTitle = title;
        OperationDetail = title;
        OperationIcon = icon;
        OperationPhase = phase ?? title;
        AddOperationFeed(title, icon, "info");
        StartProgressTicker();
    }

    private void UpdateOperation(string title, string detail, string icon)
    {
        OperationTitle = title;
        OperationDetail = detail;
        OperationIcon = icon;
        ActiveOperation = detail;
        StatusText = detail;
        AddOperationFeed(detail, icon, "info");
    }

    private void AddOperationFeed(string text, string icon, string kind)
    {
        var item = new OperationFeedItem(
            DateTimeOffset.Now.ToString("HH:mm:ss"),
            BoundStoredDetail(text),
            icon,
            kind);
        // Chronological log (newest at bottom) so the scan panel can auto-scroll down.
        OperationFeed.Add(item);
        while (OperationFeed.Count > 40)
        {
            OperationFeed.RemoveAt(0);
        }
    }

    private void PushToast(string title, string message, string icon, string kind)
    {
        var toast = new ToastNotificationViewModel(
            Guid.NewGuid().ToString("N"),
            BoundStoredDetail(title),
            BoundStoredDetail(message),
            icon,
            kind);
        Toasts.Insert(0, toast);
        while (Toasts.Count > 5)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(5200).ConfigureAwait(true);
                var match = Toasts.FirstOrDefault(item => item.Id == toast.Id);
                if (match is not null) Toasts.Remove(match);
            }
            catch
            {
                // Toast cleanup is best-effort.
            }
        });
    }

    private string ResolveModuleIcon(string moduleId) => moduleId.ToLowerInvariant() switch
    {
        "network" => "network.svg",
        "audio" => "audio.svg",
        "windows-update" or "update" => "update.svg",
        "printers" or "printer" => "printer.svg",
        "bluetooth" => "bluetooth.svg",
        "disk" or "disk-storage" => "disk.svg",
        "integrity" => "integrity.svg",
        "store" or "store-apps" => "store.svg",
        "time" or "time-sync" => "time.svg",
        "startup" or "startup-performance" or "performance" => "performance.svg",
        "camera" or "camera-privacy" => "camera.svg",
        "usb" or "usb-devices" => "usb.svg",
        "search" or "windows-search" => "search-index.svg",
        "display" or "display-graphics" => "display.svg",
        "boot" => "recovery.svg",
        _ => "search.svg"
    };

    private void PublishScanSummary(int findingCount, int passedCount)
    {
        var common = Findings.Count(finding => IsCommonOrAdvisoryFinding(finding.Model));
        var actionable = Findings.Count(finding => finding.IsActionable && !IsCommonOrAdvisoryFinding(finding.Model));
        OperationFindingCount = findingCount;
        OperationPassedCount = passedCount;
        OperationCommonFindingCount = common;
        OperationActionableFindingCount = actionable;

        if (findingCount == 0)
        {
            OperationSummaryHeadline = _text.Get("Operation_ResultHealthy");
            OperationSummaryDetail = _text.Get("Operation_ResultHealthyDetail", passedCount);
            OperationSummaryAdvice = _text.Get("Operation_ResultHealthyAdviceGuided");
            OperationResultIcon = "check.svg";
        }
        else if (actionable == 0)
        {
            OperationSummaryHeadline = _text.Get("Operation_ResultAdvisoryOnly");
            OperationSummaryDetail = _text.Get("Operation_ResultAdvisoryDetail", findingCount, common);
            OperationSummaryAdvice = _text.Get("Operation_ResultAdvisoryAdvice");
            OperationResultIcon = "info.svg";
        }
        else if (common > 0)
        {
            OperationSummaryHeadline = _text.Get("Operation_ResultMixed");
            OperationSummaryDetail = _text.Get("Operation_ResultMixedDetail", actionable, common, passedCount);
            OperationSummaryAdvice = _text.Get("Operation_ResultMixedAdvice");
            OperationResultIcon = "alert.svg";
        }
        else
        {
            OperationSummaryHeadline = _text.Get("Operation_ResultIssues");
            OperationSummaryDetail = _text.Get("Operation_ResultIssuesDetail", actionable, passedCount);
            OperationSummaryAdvice = _text.Get("Operation_ResultIssuesAdvice");
            OperationResultIcon = "alert.svg";
        }

        OperationTitle = OperationSummaryHeadline;
        OperationDetail = OperationSummaryDetail;
        OperationIcon = OperationResultIcon;
        OperationPhase = _text.Get("Operation_PhaseComplete");
        ShowOperationSummary = true;
    }

    private static bool IsCommonOrAdvisoryFinding(Finding finding)
    {
        if (finding.Severity == Severity.Info) return true;

        var key = finding.MessageKey;
        return key.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("Ducking", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("Enhancement", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("Hdmi", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("WeakWifi", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("WifiBand", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("PowerSaving", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("OldDriver", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("Throughput", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("TcpSettings", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("LinkAt100", StringComparison.OrdinalIgnoreCase) ||
               finding.RecommendedFixIds.Count == 0;
    }

    [RelayCommand]
    private void CloseOperationSummary()
    {
        ShowOperationSummary = false;
        CloseModulePanel();
        if (Findings.Count > 0) CurrentPage = 1;
    }

    [RelayCommand]
    private void ViewFindingsFromSummary()
    {
        ShowOperationSummary = false;
        CloseModulePanel();
        CurrentPage = 1;
    }

    [RelayCommand]
    private async Task ForceRepairFromSummaryAsync()
    {
        if (IsBusy) return;
        ShowOperationSummary = false;
        CloseModulePanel();
        CurrentPage = 1;
        foreach (var finding in Findings)
        {
            if (!finding.IsActionable) continue;
            // Prefer real issues first; still include advisory when no real issues exist.
            finding.IsSelected = OperationActionableFindingCount == 0 ||
                                 !IsCommonOrAdvisoryFinding(finding.Model);
        }

        if (Findings.All(finding => !finding.IsSelected))
        {
            StatusText = _text.Get("Status_NoSelection");
            PushToast(_text.Get("Toast_Failed"), StatusText, "alert.svg", "warning");
            return;
        }

        CurrentTier = RiskTier.Safe;
        await RepairSelectedAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForceRepairAllFromSummaryAsync()
    {
        if (IsBusy) return;
        ShowOperationSummary = false;
        CloseModulePanel();
        CurrentPage = 1;
        foreach (var finding in Findings)
        {
            finding.IsSelected = finding.IsActionable;
        }

        if (Findings.All(finding => !finding.IsSelected))
        {
            StatusText = _text.Get("Status_NoSelection");
            return;
        }

        CurrentTier = RiskTier.Safe;
        await RepairSelectedAsync().ConfigureAwait(true);
    }

    private void EndOperation()
    {
        StopProgressTicker();
        CanSkipRestorePoint = false;
        _restorePointCts?.Dispose();
        _restorePointCts = null;
        IsBusy = false;
        ProgressValue = 0;
        ProgressPercentText = "0%";
        ProgressEtaText = string.Empty;
        ProgressStatusText = string.Empty;
        _toolPercent = 0;
        _expectedToolMinutes = 0;
        _currentFixId = string.Empty;
        ActiveOperation = string.Empty;
        if (!ShowOperationSummary)
        {
            OperationTitle = string.Empty;
            OperationDetail = string.Empty;
            OperationPhase = string.Empty;
            OperationIcon = "search.svg";
        }
        _isRollbackOperation = false;
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void StartProgressTicker()
    {
        StopProgressTicker();
        _progressTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _progressTicker.Tick += (_, _) =>
        {
            if (!IsBusy) return;
            // Time-based fill for long tools that print little progress (within current stage window).
            if (_toolPercent <= 0.5 && _expectedToolMinutes > 0 &&
                _currentStageKey is "FixStage_Applying" or "FixStage_Verifying" or "")
            {
                var elapsedMin = (DateTimeOffset.Now - _operationStartedAt).TotalMinutes;
                var synthetic = Math.Min(92, elapsedMin / _expectedToolMinutes * 100);
                if (synthetic > _toolPercent)
                {
                    _toolPercent = synthetic;
                }
            }

            RefreshOperationProgressDisplay();
        };
        _progressTicker.Start();
    }

    private void StopProgressTicker()
    {
        if (_progressTicker is null) return;
        _progressTicker.Stop();
        _progressTicker = null;
    }

    private void ReportScanProgress(int completed, int total, string status)
    {
        total = Math.Max(1, total);
        completed = Math.Clamp(completed, 0, total);
        _progressFloor = 0;
        _progressCeiling = 100;
        _toolPercent = completed * 100d / total;
        _expectedToolMinutes = Math.Max(1.5, total * 0.35);
        ProgressStatusText = status;
        SetOperationProgress(_toolPercent);
    }

    private void ReportFixPipelineProgress(FixProgress value)
    {
        var total = Math.Max(1, value.Total);
        var index = Math.Clamp(value.Completed, 0, total);
        // Each queue item owns an equal slice; within the slice map stages.
        var slice = 100d / total;
        var floor = index * slice;
        var ceiling = Math.Min(100, (index + 1) * slice);
        _currentFixId = value.FixId ?? string.Empty;
        _currentStageKey = value.StageKey ?? string.Empty;
        _expectedToolMinutes = EstimateToolMinutes(_currentFixId);

        double within = value.StageKey switch
        {
            "FixStage_Preparing" => 0.05,
            "FixStage_BackingUp" => 0.12,
            "FixStage_Applying" => 0.20 + 0.70 * (_toolPercent / 100d),
            "FixStage_Verifying" => 0.92,
            "FixStage_Completed" or "FixStage_Failed" => 1.0,
            _ => 0.15
        };
        within = Math.Clamp(within, 0, 1);
        _progressFloor = floor;
        _progressCeiling = ceiling;
        var overall = floor + (ceiling - floor) * within;
        var stageKey = string.IsNullOrWhiteSpace(value.StageKey) ? "FixStage_Applying" : value.StageKey;
        var titleKey = string.IsNullOrWhiteSpace(value.TitleKey) ? "Status_RepairingNow" : value.TitleKey;
        var stageLabel = _text.Get(stageKey);
        var fixTitle = _text.Get(titleKey);
        ProgressStatusText = $"{fixTitle} · {stageLabel}";
        if (_expectedToolMinutes >= 8 && stageKey == "FixStage_Applying")
        {
            ProgressStatusText += " · " + _text.Get("Operation_LongToolHint", _expectedToolMinutes.ToString("0"));
        }

        SetOperationProgress(overall);
    }

    private void SetOperationProgress(double overallPercent, bool forceEta = false)
    {
        overallPercent = Math.Clamp(overallPercent, 0, IsBusy ? 99.4 : 100);
        ProgressValue = overallPercent;
        ProgressPercentText = $"{overallPercent:0}%";
        if (!IsBusy)
        {
            ProgressEtaText = string.Empty;
            return;
        }

        var elapsed = DateTimeOffset.Now - _operationStartedAt;
        if (overallPercent < 1.2 && !forceEta)
        {
            ProgressEtaText = _expectedToolMinutes > 0
                ? _text.Get("Operation_EtaStarting", _expectedToolMinutes.ToString("0"))
                : _text.Get("Operation_EtaCalculating");
            return;
        }

        if (overallPercent >= 1.2)
        {
            var totalTicks = elapsed.TotalSeconds / (overallPercent / 100d);
            var remaining = TimeSpan.FromSeconds(Math.Max(0, totalTicks - elapsed.TotalSeconds));
            ProgressEtaText = FormatEta(remaining);
        }
    }

    private void RefreshOperationProgressDisplay()
    {
        if (!IsBusy) return;
        if (_progressCeiling > _progressFloor && _toolPercent > 0 &&
            _currentStageKey is "FixStage_Applying")
        {
            var within = 0.20 + 0.70 * (_toolPercent / 100d);
            var overall = _progressFloor + (_progressCeiling - _progressFloor) * Math.Clamp(within, 0, 1);
            SetOperationProgress(overall);
            return;
        }

        SetOperationProgress(ProgressValue);
    }

    private void TryIngestProgressFromConsole(string text)
    {
        if (!IsBusy || string.IsNullOrWhiteSpace(text)) return;
        var percent = TryParseProgressPercent(text);
        if (percent is null) return;
        if (percent.Value + 0.4 < _toolPercent && _toolPercent > 5)
        {
            // Ignore noisy regressions except near start.
            return;
        }

        _toolPercent = Math.Clamp(percent.Value, 0, 100);
        if (_progressCeiling > _progressFloor)
        {
            var within = _currentStageKey == "FixStage_Applying"
                ? 0.20 + 0.70 * (_toolPercent / 100d)
                : _toolPercent / 100d;
            var overall = _progressFloor + (_progressCeiling - _progressFloor) * Math.Clamp(within, 0, 1);
            SetOperationProgress(overall);
        }
        else
        {
            SetOperationProgress(_toolPercent);
        }
    }

    private static double? TryParseProgressPercent(string text)
    {
        var verify = ProgressVerificationRegex.Match(text);
        if (verify.Success &&
            double.TryParse(verify.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vp) &&
            vp is >= 0 and <= 100)
        {
            return vp;
        }

        // Prefer the last percentage token on the line (DISM progress bars often append it).
        Match? last = null;
        foreach (Match match in ProgressPercentRegex.Matches(text))
        {
            last = match;
        }

        if (last is { Success: true } &&
            double.TryParse(last.Groups["p"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) &&
            p is >= 0 and <= 100)
        {
            return p;
        }

        return null;
    }

    private static double EstimateToolMinutes(string fixId)
    {
        if (string.IsNullOrWhiteSpace(fixId)) return 3;
        var id = fixId.ToLowerInvariant();
        if (id.Contains("dism-sfc", StringComparison.Ordinal)) return 55;
        if (id.Contains("dism-restore", StringComparison.Ordinal) || id.Contains("restorehealth", StringComparison.Ordinal)) return 40;
        if (id.Contains("scan-health", StringComparison.Ordinal) || id.Contains("scanhealth", StringComparison.Ordinal)) return 25;
        if (id.Contains("sfc", StringComparison.Ordinal)) return 30;
        if (id.Contains("component-cleanup", StringComparison.Ordinal) || id.Contains("analyze", StringComparison.Ordinal)) return 15;
        if (id.Contains("chkdsk", StringComparison.Ordinal) || id.Contains("online-scan", StringComparison.Ordinal)) return 12;
        if (id.Contains("full-reset", StringComparison.Ordinal) || id.Contains("reset-stack", StringComparison.Ordinal)) return 8;
        if (id.Contains("integrity", StringComparison.Ordinal)) return 35;
        return 4;
    }

    private string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 25)
        {
            return _text.Get("Operation_EtaAlmostDone");
        }

        if (remaining.TotalMinutes < 1.5)
        {
            return _text.Get("Operation_EtaSeconds", Math.Max(30, (int)remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        if (minutes >= 60)
        {
            var hours = minutes / 60;
            var mins = minutes % 60;
            return _text.Get("Operation_EtaHoursMinutes", hours.ToString(CultureInfo.InvariantCulture), mins.ToString(CultureInfo.InvariantCulture));
        }

        return _text.Get("Operation_EtaMinutes", minutes.ToString(CultureInfo.InvariantCulture));
    }

    private void OnConsoleLine(object? sender, ConsoleLine line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        _pendingConsoleLines.Enqueue(line);
        Interlocked.Increment(ref _pendingConsoleLineCount);
        while (Volatile.Read(ref _pendingConsoleLineCount) > MaximumPendingConsoleLines &&
               _pendingConsoleLines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingConsoleLineCount);
        }

        ScheduleConsoleFlush(dispatcher);
    }

    private void ScheduleConsoleFlush(Dispatcher dispatcher)
    {
        if (Interlocked.CompareExchange(ref _consoleFlushScheduled, 1, 0) != 0) return;
        try
        {
            _ = dispatcher.InvokeAsync(() => FlushConsoleLines(dispatcher), DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _consoleFlushScheduled, 0);
        }
    }

    private void FlushConsoleLines(Dispatcher dispatcher)
    {
        var processed = 0;
        while (processed < ConsoleFlushBatchSize && _pendingConsoleLines.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _pendingConsoleLineCount);
            ConsoleLines.Add(new ConsoleLineViewModel(line.Timestamp.ToString("HH:mm:ss"), line.Level, line.Text));
            TryIngestProgressFromConsole(line.Text);
            processed++;
        }

        while (ConsoleLines.Count > MaximumVisibleConsoleLines) ConsoleLines.RemoveAt(0);
        if (!_pendingConsoleLines.IsEmpty)
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                _ = dispatcher.InvokeAsync(() => FlushConsoleLines(dispatcher), DispatcherPriority.Background);
            }
            else
            {
                Interlocked.Exchange(ref _consoleFlushScheduled, 0);
            }
            return;
        }

        Interlocked.Exchange(ref _consoleFlushScheduled, 0);
        if (!_pendingConsoleLines.IsEmpty) ScheduleConsoleFlush(dispatcher);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // The path remains visible in the status/report if Windows cannot open it.
        }
    }

    private static bool HasRestartedSincePendingAction(SessionManifest session)
    {
        var latestAction = session.Actions
            .Where(action => action.Applied && action.RequiresReboot && !action.Verified && !action.Undone)
            .Select(action => (DateTimeOffset?)action.AppliedAt)
            .Max();
        if (latestAction is null) return false;

        var uptime = TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));
        var approximateBootTime = DateTimeOffset.Now - uptime;
        return approximateBootTime > latestAction.Value;
    }

    private static Severity ClassifyTestProgress(TestProgress value)
    {
        if (value.StageKey is "TestStage_Failed" or "TestStage_NoSignal") return Severity.Critical;
        if (value.StageKey == "TestStage_CaptivePortal") return Severity.Warning;
        if (value.Metrics is null) return Severity.Info;
        if (value.Metrics.TryGetValue("MtuBytes", out var mtu) && mtu is > 0 and < 576) return Severity.Critical;
        if (value.Metrics.TryGetValue("LossPercent", out var loss) && loss > 20) return Severity.Critical;
        if (value.Metrics.TryGetValue("Underruns", out var underruns) && underruns > 0) return Severity.Critical;
        if (value.Metrics.TryGetValue("MtuBytes", out mtu) && mtu is >= 576 and < 1_280) return Severity.Warning;
        if (value.Metrics.TryGetValue("LossPercent", out loss) && loss > 5) return Severity.Warning;
        if (value.Metrics.TryGetValue("JitterMs", out var jitter) && jitter > 30) return Severity.Warning;
        if (value.Metrics.TryGetValue("Mbps", out var mbps) && mbps < 1) return Severity.Warning;
        if (value.Metrics.TryGetValue("Milliseconds", out var resolve) && resolve >= 1_000) return Severity.Warning;
        return Severity.Info;
    }

    private LiveMetricRowViewModel? CreateMetricRow(
        LiveTestItemViewModel item,
        TestProgress value,
        Severity severity)
    {
        if (value.Metrics is null || value.StageKey is not (
                "TestStage_Result" or
                "TestStage_Failed" or
                "TestStage_CaptivePortal" or
                "TestStage_NotAvailable" or
                "TestStage_NoSignal" or
                "TestStage_Recorded"))
        {
            return null;
        }

        var metrics = value.Metrics;
        string measurements;
        if (item.Test.Id.Equals("net.live.http", StringComparison.OrdinalIgnoreCase) &&
            TryReadMetrics(metrics, ["StatusCode", "Milliseconds", "Bytes", "Expected"], out var http))
        {
            measurements = _text.Get(
                "LiveMetrics_Http",
                http[0], http[1], http[2],
                _text.Get(http[3] >= .5 ? "TestResult_Passed" : "TestResult_Failed"));
        }
        else if (item.Test.Id.Equals("net.live.mtu", StringComparison.OrdinalIgnoreCase) &&
                 TryReadMetrics(metrics, ["MtuBytes", "PayloadBytes", "Success"], out var mtu))
        {
            measurements = mtu[2] >= .5
                ? _text.Get("LiveMetrics_Mtu", mtu[0], mtu[1])
                : _text.Get("TestResult_Failed");
        }
        else if (TryReadMetrics(metrics, ["Sent", "Received", "LossPercent", "MinMs", "AverageMs", "MaxMs", "JitterMs"], out var ping))
        {
            measurements = _text.Get(
                "LiveMetrics_Ping",
                ping[0], ping[1], ping[2], ping[3], ping[4], ping[5], ping[6]);
        }
        else if (TryReadMetrics(metrics, ["Milliseconds", "Success"], out var dns))
        {
            measurements = _text.Get(
                "LiveMetrics_Dns",
                dns[0],
                _text.Get(dns[1] >= .5 ? "TestResult_Passed" : "TestResult_Failed"));
        }
        else if (TryReadMetrics(metrics, ["Mbps", "Bytes"], out var speed))
        {
            measurements = _text.Get("LiveMetrics_Speed", speed[0], speed[1] / 1024d / 1024d);
        }
        else if (metrics.TryGetValue("Peak", out var peak))
        {
            measurements = _text.Get("LiveMetrics_AudioPeak", Math.Clamp(peak, 0, 1) * 100);
        }
        else if (metrics.TryGetValue("Underruns", out var underruns))
        {
            measurements = _text.Get("LiveMetrics_Underruns", underruns);
        }
        else
        {
            measurements = string.Join(
                " · ",
                metrics
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(16)
                    .Select(pair => $"{pair.Key}: {pair.Value.ToString("0.##", CultureInfo.CurrentCulture)}"));
        }

        return new LiveMetricRowViewModel(
            item.Test.Id,
            item.Title,
            BoundStoredDetail(value.Detail),
            BoundStoredDetail(measurements),
            severity);
    }

    private static bool TryReadMetrics(
        IReadOnlyDictionary<string, double> metrics,
        IReadOnlyList<string> keys,
        out double[] values)
    {
        values = new double[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            if (!metrics.TryGetValue(keys[index], out var value) || !double.IsFinite(value))
            {
                values = [];
                return false;
            }
            values[index] = value;
        }
        return true;
    }

    private SessionFindingRecord ToSessionFinding(Finding finding)
    {
        var module = _modules.FirstOrDefault(item => item.Info.Id.Equals(
            finding.ModuleId,
            StringComparison.OrdinalIgnoreCase));
        return new SessionFindingRecord
        {
            CheckId = finding.CheckId,
            ModuleId = finding.ModuleId,
            ModuleName = module is null ? finding.ModuleId : _text.Get(module.Info.NameKey),
            Severity = finding.Severity,
            MessageKey = finding.MessageKey,
            UserMessage = BoundStoredDetail(_text.Get(finding.MessageKey, finding.MessageArguments)),
            TechnicalDetail = BoundStoredDetail(finding.TechnicalDetail),
            Status = finding.Status
        };
    }

    private static string BoundStoredDetail(string? value)
    {
        var normalized = (value ?? string.Empty).Replace('\0', ' ');
        return normalized.Length <= MaximumStoredDetailCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, MaximumStoredDetailCharacters - 25), "\n[truncated by CaYaFix]");
    }

    private static Thresholds LoadThresholds()
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["network.driverAgeYears"] = 3,
            ["network.wifiSignalWarningPercent"] = 45,
            ["network.throughputToLinkWarningRatio"] = 0.10,
            ["disk.freeSpaceWarningPercent"] = 10
        };
        var path = Path.Combine(AppContext.BaseDirectory, "thresholds.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64 * 1024) return new Thresholds(values);
            var loaded = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new Thresholds(values);
            foreach (var section in document.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object) return new Thresholds(values);
                foreach (var property in section.Value.EnumerateObject())
                {
                    if (!property.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 1_000_000)
                    {
                        return new Thresholds(values);
                    }

                    loaded[$"{section.Name}.{property.Name}"] = number;
                }
            }

            foreach (var item in loaded) values[item.Key] = item.Value;
        }
        catch
        {
            // Built-in safe defaults remain active.
        }
        return new Thresholds(values);
    }

    private sealed class FilteredModuleDefinition : IModuleDefinition
    {
        public FilteredModuleDefinition(IModuleDefinition source, IReadOnlyList<string> checkIds)
        {
            Info = source.Info;
            Checks = source.Checks.Where(check =>
                checkIds.Any(id => id.Equals(check.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
            Fixes = source.Fixes;
            LiveTests = source.LiveTests;
            Playbooks = source.Playbooks;
        }
        public ModuleInfo Info { get; }
        public IReadOnlyList<DiagnosticCheck> Checks { get; }
        public IReadOnlyList<FixAction> Fixes { get; }
        public IReadOnlyList<LiveTest> LiveTests { get; }
        public IReadOnlyList<Playbook> Playbooks { get; }
    }
}
