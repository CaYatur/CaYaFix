// Copyright (c) 2026 CaYaDev (https://cayadev.com)
// GitHub: CaYatur (https://github.com/CaYatur)
// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CaYaFix.Core;

namespace CaYaFix.App.ViewModels;

public sealed partial class ModuleCardViewModel : ObservableObject
{
    private readonly ITextProvider _text;
    private readonly Func<ModuleCardViewModel, Task> _toggle;
    private readonly Func<ModuleCardViewModel, Task> _scan;

    public ModuleCardViewModel(
        IModuleDefinition module,
        ITextProvider text,
        Func<ModuleCardViewModel, Task> toggle,
        Func<ModuleCardViewModel, Task> scan)
    {
        Module = module;
        _text = text;
        _toggle = toggle;
        _scan = scan;
        Name = text.Get(module.Info.NameKey);
        Description = text.Get(module.Info.DescriptionKey);
        IconPath = module.Info.IconFile;
        SelectCommand = new AsyncRelayCommand(() => _toggle(this));
        ScanCommand = new AsyncRelayCommand(() => _scan(this));
        CloseCommand = new AsyncRelayCommand(async () =>
        {
            if (IsExpanded) await _toggle(this).ConfigureAwait(true);
        });
    }

    public IModuleDefinition Module { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>SVG file name under Assets/Icons (loaded by <c>SvgIcon</c>).</summary>
    public string IconPath { get; }
    public ObservableCollection<SymptomViewModel> Symptoms { get; } = [];
    public IAsyncRelayCommand SelectCommand { get; }
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand CloseCommand { get; }

    [ObservableProperty]
    private bool isExpanded;

    public void LoadSymptoms(IEnumerable<Playbook> playbooks, Func<Playbook, Task> run)
    {
        Symptoms.Clear();
        foreach (var playbook in playbooks)
        {
            Symptoms.Add(new SymptomViewModel(playbook, _text, run));
        }
    }
}

public sealed partial class FindingViewModel : ObservableObject
{
    private readonly ITextProvider _text;

    public FindingViewModel(
        Finding finding,
        string moduleName,
        IReadOnlyList<FixAction> fixes,
        ITextProvider text)
    {
        Model = finding;
        _text = text;
        ModuleName = moduleName;
        Message = text.Get(finding.MessageKey, finding.MessageArguments);
        SeverityLabel = text.Get($"Severity_{finding.Severity}");
        var recommended = fixes.Where(fix =>
            finding.RecommendedFixIds.Any(id =>
                id.Equals(fix.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
        FixChoices = recommended
            .Select(fix => new FixChoiceViewModel(fix, finding, text))
            .ToArray();
        RecommendedFixes = recommended.Length == 0
            ? "—"
            : string.Join(" · ", recommended.Select(fix => text.Get(fix.TitleKey)));
        RecommendedTier = recommended.Length == 0 ? null : recommended.Min(fix => fix.Tier);
        IsActionable = FixChoices.Any(choice => choice.IsAvailable);
        HasNoAutomaticFix = !IsActionable;
        NoAutomaticFixLabel = text.Get("Finding_NoAutomaticFix");
        IsSelected = FixChoices.Any(choice => choice.IsAvailable && choice.Fix.Tier == RiskTier.Safe);
        Refresh();
    }

    public Finding Model { get; }
    public string ModuleName { get; }
    public string Message { get; }
    public string SeverityLabel { get; }
    public string RecommendedFixes { get; }
    public IReadOnlyList<FixChoiceViewModel> FixChoices { get; }
    public RiskTier? RecommendedTier { get; }
    public bool IsActionable { get; }
    public bool HasNoAutomaticFix { get; }
    public string NoAutomaticFixLabel { get; }
    public Severity Severity => Model.Severity;
    public string TechnicalDetail => Model.TechnicalDetail;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string statusLabel = string.Empty;

    public void Refresh()
    {
        StatusLabel = _text.Get($"Status_{Model.Status}");
        OnPropertyChanged(nameof(Model));
    }
}

public sealed partial class FixChoiceViewModel : ObservableObject
{
    public FixChoiceViewModel(FixAction fix, Finding finding, ITextProvider text)
    {
        Fix = fix;
        Title = text.Get(fix.TitleKey);
        TierLabel = text.Get($"Tier_{fix.Tier}");
        IsAvailable = !fix.RequiresTargetParameter || finding.RepairParameters.ContainsKey($"{fix.Id}.target");
        IsSelected = IsAvailable;
    }

    public FixAction Fix { get; }
    public string Title { get; }
    public string TierLabel { get; }
    public RiskTier Tier => Fix.Tier;
    public bool IsAvailable { get; }

    [ObservableProperty]
    private bool isSelected;
}

public sealed class ExpertFixViewModel
{
    public ExpertFixViewModel(
        FixAction fix,
        string moduleName,
        ITextProvider text,
        Func<ExpertFixViewModel, Task> run)
    {
        Fix = fix;
        ModuleName = moduleName;
        Title = text.Get(fix.TitleKey);
        Tier = fix.Tier;
        TierLabel = text.Get($"Tier_{fix.Tier}");
        RequiresTarget = fix.RequiresTargetParameter;
        RequiresReboot = fix.RequiresReboot;
        Detail = text.Get(
            "Expert_FixFlags",
            RequiresTarget ? text.Get("Expert_TargetRequired") : text.Get("Expert_NoTargetRequired"),
            RequiresReboot ? text.Get("Expert_RestartRequired") : text.Get("Expert_NoRestartRequired"));
        RunCommand = new AsyncRelayCommand(() => run(this));
    }

    public FixAction Fix { get; }
    public string ModuleName { get; }
    public string Title { get; }
    public RiskTier Tier { get; }
    public string TierLabel { get; }
    public bool RequiresTarget { get; }
    public bool RequiresReboot { get; }
    public string Detail { get; }
    public IAsyncRelayCommand RunCommand { get; }
}

public sealed class SymptomViewModel
{
    public SymptomViewModel(Playbook playbook, ITextProvider text, Func<Playbook, Task> run)
    {
        Playbook = playbook;
        Title = text.Get(playbook.SymptomKey);
        RunCommand = new AsyncRelayCommand(() => run(playbook));
    }

    public Playbook Playbook { get; }
    public string Title { get; }
    public IAsyncRelayCommand RunCommand { get; }
}

public sealed partial class LiveTestItemViewModel : ObservableObject
{
    public LiveTestItemViewModel(LiveTest test, string moduleName, ITextProvider text, Func<LiveTestItemViewModel, Task> run)
    {
        Test = test;
        ModuleName = moduleName;
        Title = text.Get(test.TitleKey);
        IconPath = test.Id.Contains("microphone", StringComparison.OrdinalIgnoreCase)
            ? "microphone.svg"
            : test.Id.Contains("speaker", StringComparison.OrdinalIgnoreCase) || test.Id.Contains("audio", StringComparison.OrdinalIgnoreCase)
                ? "speaker.svg"
                : test.Id.Contains("speed", StringComparison.OrdinalIgnoreCase)
                    ? "performance.svg"
                    : test.Id.Contains("dns", StringComparison.OrdinalIgnoreCase) ||
                      test.Id.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                      test.Id.Contains("mtu", StringComparison.OrdinalIgnoreCase)
                        ? "network.svg"
                        : "ping.svg";
        IsAudioTest = test.ModuleId.Equals("audio", StringComparison.OrdinalIgnoreCase);
        RunCommand = new AsyncRelayCommand(() => run(this));
    }

    public LiveTest Test { get; }
    public string ModuleName { get; }
    public string Title { get; }
    public string IconPath { get; }
    public bool IsAudioTest { get; }
    public IAsyncRelayCommand RunCommand { get; }

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private double progress;

    [ObservableProperty]
    private string lastResult = string.Empty;

    [ObservableProperty]
    private Severity? resultSeverity;
}

public sealed record LiveMetricRowViewModel(
    string TestId,
    string Test,
    string Result,
    string Measurements,
    Severity Severity);

public sealed class RecoverySessionViewModel
{
    public RecoverySessionViewModel(
        SessionManifest session,
        ITextProvider text,
        IReadOnlyDictionary<string, string> fixTitles,
        Func<RecoverySessionViewModel, Task> undo,
        Func<RecoveryActionViewModel, Task> undoAction,
        Func<RecoverySessionViewModel, Task> dismiss,
        Func<RecoveryActionViewModel, Task> dismissAction,
        Action<string> openFolder)
    {
        Session = session;
        Title = session.StartedAt.ToString("g", CultureInfo.CurrentCulture);
        Subtitle = text.Get(
            "Recovery_Subtitle",
            session.Actions.Count,
            session.Findings.Count,
            text.Get($"SessionStatus_{session.Status}"));
        Actions = session.Actions
            .Select((action, index) => new RecoveryActionViewModel(
                session,
                action,
                index,
                fixTitles.TryGetValue(action.FixId, out var title) ? title : action.FixId,
                text,
                undoAction,
                dismissAction))
            .ToArray();
        IsUndoAvailable = session.Actions.Any(action => action.Applied && !action.Undone && action.Backup is not null);
        IsDismissAvailable = SessionRecoveryGates.SessionRequiresRecovery(session);
        UndoCommand = new AsyncRelayCommand(() => undo(this), () => IsUndoAvailable);
        DismissCommand = new AsyncRelayCommand(() => dismiss(this), () => IsDismissAvailable);
        OpenFolderCommand = new RelayCommand(() => openFolder(session.DirectoryPath));
    }

    public SessionManifest Session { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<RecoveryActionViewModel> Actions { get; }
    public bool IsUndoAvailable { get; }
    public bool IsDismissAvailable { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand DismissCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
}

public sealed class RecoveryActionViewModel
{
    public RecoveryActionViewModel(
        SessionManifest session,
        SessionActionRecord action,
        int actionIndex,
        string title,
        ITextProvider text,
        Func<RecoveryActionViewModel, Task> undo,
        Func<RecoveryActionViewModel, Task> dismiss)
    {
        Session = session;
        ActionIndex = actionIndex;
        Title = title;
        var state = action.ResultMessageKey.Equals("FixResult_RecoveryDismissed", StringComparison.Ordinal)
            ? text.Get("Recovery_ActionDismissed")
            : action.Undone
                ? text.Get("Recovery_ActionUndone")
                : !action.Applied
                    ? text.Get("Recovery_ActionNotApplied")
                    : action.Verified
                        ? text.Get("Recovery_ActionVerified")
                        : action.RequiresReboot
                            ? text.Get("Recovery_ActionPendingRestart")
                            : text.Get("Recovery_ActionApplied");
        Subtitle = text.Get(
            "Recovery_ActionSubtitle",
            text.Get($"Tier_{action.Tier}"),
            action.AppliedAt.ToString("g", CultureInfo.CurrentCulture),
            state);
        IsUndoAvailable = action.Applied && !action.Undone && action.Backup is not null;
        IsDismissAvailable = SessionRecoveryGates.RequiresRecovery(action);
        UndoCommand = new AsyncRelayCommand(() => undo(this));
        DismissCommand = new AsyncRelayCommand(() => dismiss(this));
    }

    public SessionManifest Session { get; }
    public int ActionIndex { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsUndoAvailable { get; }
    public bool IsDismissAvailable { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand DismissCommand { get; }
}

public sealed record ConsoleLineViewModel(string Time, string Level, string Text);

public sealed record OperationFeedItem(string Time, string Text, string Icon, string Kind);

public sealed partial class ToastNotificationViewModel : ObservableObject
{
    public ToastNotificationViewModel(string id, string title, string message, string icon, string kind)
    {
        Id = id;
        Title = title;
        Message = message;
        Icon = icon;
        Kind = kind;
    }

    public string Id { get; }
    public string Title { get; }
    public string Message { get; }
    public string Icon { get; }
    public string Kind { get; }
}
