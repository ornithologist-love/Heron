using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

/// <summary>
/// Host 「自动化」 page: task list → task detail (settings + run history) → run detail (step timeline).
/// All data comes from <see cref="AutomationHost"/>; this class only projects it onto the UI thread.
/// </summary>
public sealed partial class AutomationPageViewModel : ViewModelBase, IDisposable, Heron.ClientFramework.Abstractions.IClientPageActivity
{
    private volatile bool _pageActive;

    public void SetPageActive(bool active)
    {
        _pageActive = active;
        NotifyDeferredContent();
        _refresh.Stop();
        if (active)
        {
            UpdateScriptDirectories();
            NotifyTaskScriptReload();
            Refresh();
            if (ShowScriptBrowser && !IsScriptBusy) RefreshScriptsCommand.Execute(null);
        }
        else
        {
            _scriptInspection?.Cancel();
            Editor?.CancelFieldChoices();
            _draftEditor?.CancelFieldChoices();
        }
    }
    private readonly AutomationHost _host;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _historyKey;
    private bool _selectNewestRun;
    // The single unsaved 「新建」 row and its form. Kept while the user looks at other tasks; gone on save/discard.
    private AutomationTaskItemViewModel? _draft;
    private AutomationEditorViewModel? _draftEditor;

    public AutomationPageExtensions Extensions { get; }
    public bool HasGlobalSettings => Extensions.CreateSettingsPanel != null;
    public bool CanUploadWorkflow => Extensions.CreateWorkflowUploadPanel != null;

    public AutomationPageViewModel(AutomationHost host, AutomationPageExtensions? extensions = null)
    {
        _host = host;
        Extensions = extensions ?? new();
        Logs = new AutomationLogsViewModel(() => ExportEvidenceAsync(SelectedRun));
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (_pageActive) Refresh();
        };
        _host.Changed += OnHostChanged;
        InitializeScripts();
        Refresh();
    }

    public bool IsSchedulingPaused => !_host.IsSchedulingEnabled;
    public ObservableCollection<AutomationTaskItemViewModel> Tasks { get; } = [];
    public ObservableCollection<AutomationTemplateOption> Templates { get; } = [];
    public ObservableCollection<AutomationRunItemViewModel> Runs { get; } = [];
    public AutomationArtifactsViewModel Artifacts { get; } = new();
    public AutomationLogsViewModel Logs { get; }
    private AutomationRunItemViewModel? _artifactRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanRunSelected), nameof(CanCancelSelected), nameof(CanDeleteSelected))]
    private AutomationTaskItemViewModel? _selectedTask;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasEditor), nameof(ShowSaveActions))] private AutomationEditorViewModel? _editor;
    public bool HasEditor => Editor != null;

    internal void RefreshGlobalSettings()
    {
        Editor?.RefreshGlobalSettings();
        if (_draftEditor != Editor) _draftEditor?.RefreshGlobalSettings();
        Refresh();
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(ShowArtifactView), nameof(ShowWorkflowView), nameof(ShowLaunchParameters))]
    private AutomationRunItemViewModel? _selectedRun;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowArtifactView), nameof(ShowWorkflowView), nameof(ShowLaunchParameters))]
    private int _runViewIndex;
    public bool ShowArtifactView => HasRun && Artifacts.HasArchive && RunViewIndex == 2;
    public bool ShowLaunchParameters => HasRun && RunViewIndex == 1;
    public bool ShowWorkflowView => !ShowArtifactView && !ShowLaunchParameters;
    /// <summary>「任务编排」视图：右侧运行区改为展示当前设置将执行的流程图，而不是某次运行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(RunAreaBadge), nameof(RunAreaTitle), nameof(ShowArtifactView), nameof(ShowWorkflowView), nameof(ShowLaunchParameters))]
    private bool _showPlan;
    [ObservableProperty] private bool _isTemplateMenuOpen;

    [ObservableProperty] private string _subgraphTitle = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSubgraph))]
    private ObservableCollection<AutomationStepItemViewModel>? _subgraphSteps;
    private readonly Stack<(string Title, ObservableCollection<AutomationStepItemViewModel> Steps)> _graphHistory = new();
    public bool HasSubgraph => SubgraphSteps != null;

    internal void RefreshTheme()
    {
        foreach (var task in Tasks) task.RefreshTheme();
        foreach (var run in Runs) run.RefreshTheme();
        if (SubgraphSteps != null)
            foreach (var step in SubgraphSteps) step.RefreshTheme();
        foreach (var graph in _graphHistory)
            foreach (var step in graph.Steps) step.RefreshTheme();
        Editor?.RefreshTheme();
    }

    [RelayCommand]
    private void OpenSubgraph(object? item)
    {
        var children = item switch
        {
            AutomationStepItemViewModel step when step.HasChildren => step.Children,
            AutomationStepChip chip when chip.HasChildren => new ObservableCollection<AutomationStepItemViewModel>(
                chip.Children!.Select(s => new AutomationStepItemViewModel(AutomationStepItemViewModel.Preview(s), isPlan: true))),
            _ => null
        };
        if (children == null) return;
        if (SubgraphSteps != null) _graphHistory.Push((SubgraphTitle, SubgraphSteps));
        SubgraphTitle = item is AutomationStepItemViewModel live ? live.Title : ((AutomationStepChip)item!).Title;
        SubgraphSteps = children;
    }

    [RelayCommand]
    private void BackFromSubgraph()
    {
        if (_graphHistory.TryPop(out var previous))
        {
            SubgraphTitle = previous.Title;
            SubgraphSteps = previous.Steps;
        }
        else SubgraphSteps = null;
    }

    /// <summary>图上 HumanGate 节点的「审核」：回到主图并请求视图把人工审核面板滚入可见区域。</summary>
    public event EventHandler? ReviewFocusRequested;

    [RelayCommand]
    private void FocusReview()
    {
        if (SelectedRun?.HasReview != true) return;
        CloseSubgraph();
        RunViewIndex = 0;
        ReviewFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseSubgraph()
    {
        _graphHistory.Clear();
        SubgraphSteps = null;
    }

    partial void OnSelectedRunChanging(AutomationRunItemViewModel? oldValue, AutomationRunItemViewModel? newValue) =>
        oldValue?.UnloadDetails();

    private void LoadSelectedRunDetails()
    {
        if (SelectedRun is not { } run) return;
        var summary = run.HistorySummary;
        if (!run.DetailsLoaded || run.Record.Revision != summary.Revision || run.Status != summary.Status || !summary.IsFinished)
            run.LoadDetails(_host.FindRun(summary.TaskId, summary.RunId) ?? summary);
    }

    partial void OnSelectedRunChanged(AutomationRunItemViewModel? value)
    {
        LoadSelectedRunDetails();
        ShowRunLogs = false;
        IsRunReportExpanded = false;
        if (_artifactRun != null) _artifactRun.PropertyChanged -= OnArtifactRunChanged;
        _artifactRun = value;
        if (_artifactRun != null) _artifactRun.PropertyChanged += OnArtifactRunChanged;
        Artifacts.SetRun(value?.Record);
        Logs.SetRun(value?.Record);
        RunViewIndex = HasCompletedMarkdownReport(value) ? 2 : 0;
        // Picking a run (from the list or live refresh) always leaves the plan view.
        if (value != null && !_updatingHistory) { PageTabIndex = 2; ShowPlan = false; }
        CloseSubgraph();
        ReviewFeedback = "";
        _submittedReviewId = null;
        ReviewError = "";
        ShowReviewSettings = false;
        NotifyReviewLayout();
        OnPropertyChanged(nameof(CanReviewSelected));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(ShowManualRun));
    }

    private void OnArtifactRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutomationRunItemViewModel.Status)) NotifyReexecution();
        if (e.PropertyName is nameof(AutomationRunItemViewModel.ArtifactPath) or nameof(AutomationRunItemViewModel.RegisteredArtifacts))
        {
            Artifacts.SetRun(SelectedRun?.Record);
            if (HasCompletedMarkdownReport(SelectedRun) && RunViewIndex != 1) RunViewIndex = 2;
            else if (!Artifacts.HasArchive && RunViewIndex == 2) RunViewIndex = 0;
            OnPropertyChanged(nameof(ShowArtifactView));
            OnPropertyChanged(nameof(ShowWorkflowView));
        }
        else if (e.PropertyName == nameof(AutomationRunItemViewModel.Status)
                 && SelectedRun?.Status == AutomationRunStatus.AwaitingReview && !(ShowRunLogs && IsRunReportExpanded))
            RunViewIndex = 0;
    }

    private static bool HasCompletedMarkdownReport(AutomationRunItemViewModel? run) =>
        run?.Status is AutomationRunStatus.Succeeded or AutomationRunStatus.Skipped
        && System.IO.Path.GetExtension(run.ArtifactPath)?.ToLowerInvariant() is ".md" or ".markdown";

    public bool HasTasks => Tasks.Count > 0;
    public bool HasSelection => SelectedTask != null;
    /// <summary>Selected row is a saved task (not the draft).</summary>
    private AutomationTaskItemViewModel? SavedTask => SelectedTask is { IsDraft: false } task ? task : null;
    public bool IsSelectedTaskInvalid => SavedTask is { Availability: AutomationTaskAvailability.InvalidTask };
    /// <summary>右侧主区展示某次运行的详情（而非任务编排）。</summary>
    public bool HasRun => SelectedRun != null && !ShowPlan;
    public bool HasRuns => Runs.Count > 0;
    public bool HasUnsavedChanges => Editor is { IsDirty: true } or { IsNew: true };
    public bool ShowSaveActions => HasEditor && (IsParametersTab || HasUnsavedChanges);
    [RelayCommand]
    private void ShowPlanView()
    {
        if (!HasEditor) return;
        ShowRunLogs = false;
        ShowPlan = true;
        PageTabIndex = 1;
        CloseSubgraph();
    }

    [RelayCommand]
    private void ViewHistory()
    {
        if (SavedTask == null) return;
        ShowPlan = false;
        PageTabIndex = 2;
        CloseSubgraph();
        ReloadHistory();
        SelectedRun ??= Runs.FirstOrDefault();
    }

    /// <summary>「任务编排」可用：有编辑器（即有模板）就能画出当前设置的流程。</summary>
    public bool ShowPlanEntry => HasEditor;
    /// <summary>右侧主区徽标：编排视图显示“编排”，否则跟随选中运行的状态。</summary>
    public string RunAreaBadge => ShowPlan ? "任务编排" : SelectedRun?.StatusText
        ?? (SavedTask is { CanExecute: false } task ? task.AvailabilityText : "未运行");
    public string RunAreaEmptyText => IsPlanTab && !HasEditor ? "模板尚未加载，请重新加载脚本后查看编排"
        : SavedTask is { CanExecute: false } ? "暂无运行历史" : "按当前设置将执行以下流程";
    /// <summary>右侧主区标题：编排视图显示模板名，否则显示选中运行的时间标题。</summary>
    public string RunAreaTitle => ShowPlan
        ? Editor?.Heading ?? SelectedRun?.Title ?? ""
        : SelectedRun?.Title ?? "";

    /// <summary>Run the saved settings after edits have been saved or discarded.</summary>
    public bool CanRunSelected => SavedTask is { CanExecute: true, ManualEnabled: true } task
        && (task.AllowParallel || !task.IsActive && !task.HasPendingReview) && !HasUnsavedChanges && !IsScriptBusy;
    public bool ShowManualRun => SavedTask is { CanExecute: true, ManualEnabled: true } && !HasUnsavedChanges
        && (!CanCancelSelected || SavedTask.AllowParallel);
    public string RunButtonTip => "通过手动触发执行一次，执行过程和结果记入运行记录";
    public bool CanCancelSelected => SavedTask is { } task && SelectedRun is { } run
        && task.CurrentRuns.Any(r => r.RunId == run.RunId);
    public bool CanDeleteSelected => SelectedTask != null;
    public string DeleteTip => SelectedTask is { IsDraft: true } ? "放弃这个草稿" : "删除任务和运行记录，并取消正在执行的运行；保留日志与产物";
    /// <summary>Copy the selected saved task into a new draft (same template, other target / options / time).</summary>
    public bool CanDuplicateSelected => !IsSelectedTaskInvalid && SavedTask != null && Editor is { IsNew: false };

    public string HeaderTitle => SelectedTask?.Name is { Length: > 0 } name ? name : Editor?.Heading ?? "";
    public string HeaderSubtitle => Editor == null
        ? SelectedTask == null ? "" : SelectedTask.Title
        : Editor.Description;
    public FluentIcons.Common.Icon HeaderIcon => Editor?.Icon ?? SelectedTask?.Icon ?? FluentIcons.Common.Icon.CalendarClock;
    public bool HasSelectedTask => SavedTask != null;
    public bool IsDraft => SelectedTask is { IsDraft: true };

    public string EmptyText => Templates.Count == 0
        ? "暂无已注册的任务模板。可在「脚本管理」加载兼容本机的脚本，或启用支持当前平台的功能插件。"
        : "还没有自动化任务。点击右上角「新建任务」，选择要自动做的事和运行频率、时间。同一流程可以保存多条启动配置，使用不同参数与定时规则。";

    /// <summary>
    /// Shell entry. A task id selects that task. Otherwise the first task of the template (and matching parameters, when given)
    /// is selected; when there is none a new draft of the template opens, using the supplied parameters.
    /// </summary>
    public void Focus(string? taskId, string? templateId, IReadOnlyDictionary<string, string>? parameters = null)
    {
        Refresh();
        if (taskId != null && Tasks.FirstOrDefault(t => t.Id == taskId) is { } existing)
        {
            SelectedTask = existing;
            return;
        }
        if (templateId == null) return;
        var match = _host.Tasks.FirstOrDefault(v => v.Task.TemplateId == templateId
            && (parameters == null || parameters.All(p => v.Task.Parameter(p.Key) == p.Value)));
        if (match != null && Tasks.FirstOrDefault(t => t.Id == match.Task.Id) is { } item)
        {
            SelectedTask = item;
            return;
        }
        if (Templates.FirstOrDefault(t => t.Id == templateId) is { } template)
            StartDraft(template.Template, null, parameters);
    }

    [RelayCommand]
    private void NewTask(AutomationTemplateOption? template)
    {
        IsTemplateMenuOpen = false;
        if (template != null) StartDraft(template.Template, null, null);
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (!CanDuplicateSelected || SelectedTask == null) return;
        var view = _host.Tasks.FirstOrDefault(t => t.Task.Id == SelectedTask.Id);
        if (view?.Template != null) StartDraft(view.Template, view.Task, null);
    }

    /// <summary>
    /// Adds the 「草稿」 row at the top of the list and selects it. Only one draft exists: a new one replaces the old.
    /// The row turns into the real task on save.
    /// </summary>
    private void StartDraft(IAutomationTemplate template, AutomationTask? seed, IReadOnlyDictionary<string, string>? parameters)
    {
        RemoveDraft();
        seed ??= _host.ResolveTaskConfiguration(new AutomationTask { TemplateId = template.Id });
        _draftEditor = new AutomationEditorViewModel(template, null, SaveEditor, OnEditorChanged, _host.Check,
            _host.SuggestName(template.Id), seed, parameters);
        _draft = new AutomationTaskItemViewModel("draft-" + Guid.NewGuid().ToString("N"), isDraft: true);
        _draft.ApplyDraft(_draftEditor);
        Tasks.Insert(0, _draft);
        RefreshTaskList();
        OnPropertyChanged(nameof(HasTasks));
        SelectedTask = _draft;
    }

    private void RemoveDraft()
    {
        if (_draft == null) return;
        var draft = _draft;
        _draft = null;
        _draftEditor = null;
        if (ReferenceEquals(SelectedTask, draft)) SelectedTask = null;
        Tasks.Remove(draft);
        RefreshTaskList();
        OnPropertyChanged(nameof(HasTasks));
    }

    [RelayCommand]
    private void RunSelected()
    {
        if (!CanRunSelected || SavedTask is not { } task) return;
        ApplySettingsRuleOnRun();
        _selectNewestRun = true;
        if (!_host.RunNow(task.Id, "manual") && task.Problem == null)
            task.Flash(task.HasPendingReview ? "请先审核或放弃当前草稿" : "任务已在队列中");
        _historyKey = null;
        Refresh();
    }

    [RelayCommand]
    private void DiscardEdits()
    {
        if (SelectedTask is { IsDraft: true }) { RemoveDraft(); SelectedTask = Tasks.FirstOrDefault(); return; }
        if (SelectedTask is { } task) { SelectedTask = null; SelectedTask = task; }
    }

    [RelayCommand]
    private void CancelSelected()
    {
        if (CanCancelSelected && SavedTask != null && SelectedRun != null) _host.Cancel(SavedTask.Id, SelectedRun.RunId);
    }

    /// <summary>「保存检查」 points at the clashing task: select it so the user can edit that one instead.</summary>
    [RelayCommand]
    private void OpenConflict(AutomationViolationItem? violation)
    {
        if (violation?.ConflictTaskId is { } id && Tasks.FirstOrDefault(t => t.Id == id) is { } task)
            SelectedTask = task;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (!CanDeleteSelected) return;
        if (SelectedTask is { IsDraft: true }) { DiscardEdits(); return; }
        if (SelectedTask is not { } task) return;
        try
        {
            _host.Delete(task.Id);
            SelectedTask = null;
            Editor = null;
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            task.Flash(ex.Message);
        }
    }

    partial void OnSelectedTaskChanged(AutomationTaskItemViewModel? value)
    {
        // A collection move can temporarily clear ListBox selection; keep the form and unsaved edits.
        if (_taskRowsMoving) return;
        TaskScriptMessage = "";
        _selectedScriptEntry = null;
        if (value != null) ShowScriptBrowser = false;
        CloseSubgraph();
        ShowPlan = false;
        if (Editor != _draftEditor) Editor?.CancelFieldChoices();
        if (value is { IsDraft: true })
        {
            Editor = _draftEditor;
        }
        else if (value != null)
        {
            var view = _host.Tasks.FirstOrDefault(t => t.Task.Id == value.Id);
            Editor = view?.Template == null ? null
                : new AutomationEditorViewModel(view.Template, view.Task, SaveEditor, OnEditorChanged, _host.Check);
            if (view != null) Editor?.SetCloudTask(_host.CloudTask(view.Task));
        }
        else
        {
            Editor = null;
        }
        _historyKey = null;
        ReloadHistory();
        ApplySettingsRuleOnSelect();
        NotifySelection();
        _ = DetectSelectedScriptChangeAsync();
    }

    private void SaveEditor(AutomationTask task)
    {
        if (Editor == null) return;
        var warnings = Editor?.Violations.Select(v => v.Violation).ToArray() ?? [];
        var wasNew = Editor is { IsNew: true };
        _host.Save(task);
        // Saving the draft turns its row into the real one: drop the placeholder, then select the saved task.
        if (_draftEditor != null && ReferenceEquals(Editor, _draftEditor)) RemoveDraft();
        Refresh();
        SelectedTask = Tasks.FirstOrDefault(t => t.Id == task.Id);
        // Warnings were accepted by saving; keep them visible on the saved task's form.
        if (warnings.Length > 0) Editor?.ShowViolations(warnings);
        ApplySettingsRuleAfterSave(wasNew, saved: true);
    }

    /// <summary>编辑器“保存”按钮：保存失败时按规则展开并定位到出错的分组。</summary>
    [RelayCommand]
    private void SaveSettings()
    {
        if (Editor is not { } editor) return;
        if (!editor.TrySave()) ApplySettingsRuleAfterSave(editor.IsNew, saved: false);
        NotifySettingsSummary();
    }

    private void OnEditorChanged()
    {
        if (_draft != null && _draftEditor != null) _draft.ApplyDraft(_draftEditor);
        RefreshTaskList();
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(CanRunSelected));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ShowSaveActions));
        OnPropertyChanged(nameof(ShowManualRun));
        OnPropertyChanged(nameof(RunButtonTip));
        NotifyTaskScriptReload();
        NotifySettingsSummary();
    }

    private void OnHostChanged()
    {
        if (!_pageActive) return;
        // Host events arrive from worker threads and can be frequent (progress); coalesce on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (_pageActive && !_refresh.IsEnabled) _refresh.Start();
        });
    }

    private void Refresh()
    {
        var templates = _host.Templates;
        if (!templates.SequenceEqual(Templates.Select(t => t.Template), ReferenceEqualityComparer.Instance))
        {
            Templates.Clear();
            foreach (var template in templates) Templates.Add(new AutomationTemplateOption(template));
        }

        var views = SortByTemplate(_host.Tasks);
        // Protect the hold interval and drag from background list merges, just like workspace discovery.
        if (!_isTaskReordering) MergeTaskRows(views);
        foreach (var view in views)
        {
            var item = Tasks.FirstOrDefault(t => t.Id == view.Task.Id);
            if (item == null) continue;
            item.Apply(view);
            if (SelectedTask?.Id == view.Task.Id)
            {
                if (view.Template == null)
                {
                    if (Editor != null) CloseSubgraph();
                    Editor?.CancelFieldChoices();
                    Editor = null;
                    ShowPlan = false;
                    ShowReviewSettings = false;
                    ShowSettingsHint = false;
                }
                else if (Editor == null || (Editor is { IsNew: false, IsDirty: false }
                    && (!Editor.UsesTemplate(view.Template) || Editor.ManualEnabled != view.Task.ManualEnabled
                        || Editor.ScheduleEnabled != view.Task.ScheduleEnabled || !Editor.MatchesTriggers(view.Task))))
                {
                    Editor?.CancelFieldChoices();
                    Editor = new AutomationEditorViewModel(view.Template, view.Task, SaveEditor, OnEditorChanged, _host.Check);
                    CloseSubgraph();
                    ApplySettingsRuleOnSelect();
                }
                Editor?.SetCloudTask(_host.CloudTask(view.Task));
            }
        }

        if (!_isTaskReordering) RefreshTaskList();
        RefreshTemplateList();
        ReloadHistory();
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(CanResetTaskOrder));
        OnPropertyChanged(nameof(EmptyText));
        NotifySelection();
    }

    /// <summary>Default order: grouped by template kind, alphabetical by template title; stable within one kind.</summary>
    internal static IReadOnlyList<AutomationTaskView> SortByTemplate(IReadOnlyList<AutomationTaskView> views) =>
        views.OrderBy(v => v.Template?.Title ?? v.Task.TemplateId, StringComparer.CurrentCulture).ToArray();

    /// <summary>The list keeps metadata; only the selected run materializes its steps and review body.</summary>
    private void ReloadHistory()
    {
        var task = SavedTask;
        if (task == null)
        {
            if (Runs.Count > 0) { Runs.Clear(); OnPropertyChanged(nameof(HasRuns)); }
            SelectedRun = null;
            return;
        }
        var records = _host.HistorySummaries(task.Id);
        var key = task.Id + "|" + string.Join('|', records.Select(r => $"{r.RunId}:{r.Status}"));
        if (key != _historyKey)
        {
            _historyKey = key;
            var selectedId = SelectedRun?.RunId;
            var existingRuns = Runs.ToDictionary(run => run.RunId);
            Runs.Clear();
            foreach (var record in records)
            {
                if (existingRuns.TryGetValue(record.RunId, out var existing))
                {
                    existing.ApplySummary(record);
                    Runs.Add(existing);
                }
                else Runs.Add(new AutomationRunItemViewModel(record));
            }
            _updatingHistory = true;
            try
            {
                SelectedRun = (_selectNewestRun ? null : Runs.FirstOrDefault(r => r.RunId == selectedId))
                    ?? Runs.FirstOrDefault();
            }
            finally { _updatingHistory = false; }
            if (_selectNewestRun) { PageTabIndex = 2; ShowPlan = false; }
            _selectNewestRun = false;
            OnPropertyChanged(nameof(HasRuns));
        }
        foreach (var record in records)
            if (Runs.FirstOrDefault(r => r.RunId == record.RunId) is { } live) live.ApplySummary(record);
        LoadSelectedRunDetails();
        Logs.SetRun(SelectedRun?.Record);
        SynchronizeReviewFeedback();
        NotifyReexecution();
        OnPropertyChanged(nameof(CanReviewSelected));
    }

    private void NotifySelection()
    {
        NotifyTaskScriptReload();
        OnPropertyChanged(nameof(ShowTaskDetails));
        OnPropertyChanged(nameof(ShowPlanEntry));
        OnPropertyChanged(nameof(RunAreaBadge));
        OnPropertyChanged(nameof(RunAreaEmptyText));
        OnPropertyChanged(nameof(IsSelectedTaskInvalid));
        OnPropertyChanged(nameof(RunAreaTitle));
        OnPropertyChanged(nameof(ShowTaskEmpty));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRunSelected));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanDuplicateSelected));
        OnPropertyChanged(nameof(DeleteTip));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ShowSaveActions));
        OnPropertyChanged(nameof(ShowManualRun));
        OnPropertyChanged(nameof(RunButtonTip));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(HeaderIcon));
        OnPropertyChanged(nameof(HasSelectedTask));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(CanReviewSelected));
        NotifyReviewLayout();
    }

    public void Dispose()
    {
        _scriptInspection?.Cancel();
        _scriptInspection?.Dispose();
        if (_artifactRun != null) _artifactRun.PropertyChanged -= OnArtifactRunChanged;
        Artifacts.Dispose();
        Logs.Dispose();
        Editor?.CancelFieldChoices();
        _draftEditor?.CancelFieldChoices();
        _host.Changed -= OnHostChanged;
        _host.Scripts.Changed -= OnScriptsChanged;
        _refresh.Stop();
    }
}

public sealed class AutomationTemplateOption(IAutomationTemplate template)
{
    public IAutomationTemplate Template { get; } = template;
    public string Id => Template.Id;
    public string Title => Template.Title;
    public string Description => Template.Description;
    public FluentIcons.Common.Icon Icon => AutomationIcons.Parse(Template.Icon);
}

internal static class AutomationIcons
{
    public static FluentIcons.Common.Icon Parse(string name) =>
        Enum.TryParse<FluentIcons.Common.Icon>(name, out var icon) ? icon : FluentIcons.Common.Icon.CalendarClock;
}
