using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heron.ClientControls.Graphs;
using Heron.Pregel;

namespace Heron.Pregel.Avalonia.ViewModels;

/// <summary>One row of the task list: what, parameters, when, and how the last run went.</summary>
public sealed partial class AutomationTaskItemViewModel(string id, bool isDraft = false) : ObservableObject
{
    private CancellationTokenSource? _flash;

    public string Id { get; } = id;

    /// <summary>Unsaved 「新建」 row: lives only in the page, turns into a real row when saved.</summary>
    public bool IsDraft { get; } = isDraft;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _schedule = "";
    [ObservableProperty] private string _listSchedule = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private AutomationRunStatus? _statusKind;
    [ObservableProperty] private FluentIcons.Common.Icon _icon;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private bool _manualEnabled = true;
    [ObservableProperty] private IReadOnlyList<AutomationTriggerBadge> _triggerBadges = [];
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private AutomationTaskAvailability _availability;
    [ObservableProperty] private double? _progress;

    // ===== 列表卡片（样式见 Heron.ClientControls/Themes/AutomationTaskCard.axaml） =====
    [ObservableProperty] private string _cardStatus = "";
    [ObservableProperty] private string _cardTime = "";
    [ObservableProperty] private AutomationCardTone _cardTone = AutomationCardTone.Idle;
    public bool HasCardStatus => CardTone != AutomationCardTone.None && CardStatus.Length > 0;
    public bool IsCardIdle => CardTone == AutomationCardTone.Idle;
    public bool IsCardQueued => CardTone == AutomationCardTone.Queued;
    public bool IsCardRunning => CardTone == AutomationCardTone.Running;
    public bool IsCardReview => CardTone == AutomationCardTone.Review;
    public bool IsCardSucceeded => CardTone == AutomationCardTone.Succeeded;
    public bool IsCardFailed => CardTone == AutomationCardTone.Failed;
    public bool IsCardCancelled => CardTone == AutomationCardTone.Cancelled;
    public bool IsUnavailable => !IsDraft && !CanExecute;
    public bool HasTriggerBadges => TriggerBadges.Count > 0;
    public bool IsProgressIndeterminate => Progress == null;
    partial void OnCardToneChanged(AutomationCardTone value)
    {
        OnPropertyChanged(nameof(HasCardStatus));
        OnPropertyChanged(nameof(IsCardIdle));
        OnPropertyChanged(nameof(IsCardQueued));
        OnPropertyChanged(nameof(IsCardRunning));
        OnPropertyChanged(nameof(IsCardReview));
        OnPropertyChanged(nameof(IsCardSucceeded));
        OnPropertyChanged(nameof(IsCardFailed));
        OnPropertyChanged(nameof(IsCardCancelled));
    }
    partial void OnCardStatusChanged(string value) => OnPropertyChanged(nameof(HasCardStatus));
    partial void OnTriggerBadgesChanged(IReadOnlyList<AutomationTriggerBadge> value) => OnPropertyChanged(nameof(HasTriggerBadges));

    public AutomationRunRecord? Current { get; private set; }
    public IReadOnlyList<AutomationRunRecord> CurrentRuns { get; private set; } = [];
    public bool AllowParallel { get; private set; }
    private bool _hasPendingReview;
    public string? CurrentRunId => Current?.RunId;
    public string? LastRunId { get; private set; }
    public AutomationRunRecord? Last { get; private set; }
    public bool HasPendingReview => _hasPendingReview;
    public bool HasProgress => Progress != null;
    public IBrush StatusBrush => AutomationPalette.Status(StatusKind, StatusKind == null && Problem != null);
    public IBrush ListStatusBrush => AutomationPalette.Status(StatusKind, false);
    internal void RefreshTheme()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ListStatusBrush));
    }
    public bool IsScheduleInvalid => Availability is AutomationTaskAvailability.TemplateLoadFailed
        or AutomationTaskAvailability.ValidationFailed or AutomationTaskAvailability.InvalidTask;
    public bool CanExecute => Availability == AutomationTaskAvailability.Available;
    public string AvailabilityText => Availability switch
    {
        AutomationTaskAvailability.TemplateNotLoaded => "模板未加载",
        AutomationTaskAvailability.TemplateLoadFailed => "模板加载失败",
        AutomationTaskAvailability.TemplateNeedsReload => "需重新加载",
        AutomationTaskAvailability.ValidationFailed => "默认启动配置未通过",
        AutomationTaskAvailability.InvalidTask => "已失效",
        _ => "可用"
    };
    public string ListDetail => $"{Name}\n{Title}\n{Schedule}\n{Status}";
    public string ListStatus => _flash != null || IsDraft ? Status
        : IsActive ? CurrentRuns.Count > 1 ? ActiveStatus : AutomationPalette.StatusText(StatusKind ?? AutomationRunStatus.Queued)
        : Last is { } run ? $"{AutomationPalette.StatusText(run.Status)} · {FormatWhen(run.EndedAt ?? run.QueuedAt)}"
        : "尚未运行";
    private string ActiveStatus => $"{CurrentRuns.Count(r => r.Status == AutomationRunStatus.Running)} 个运行中 · {CurrentRuns.Count(r => r.Status == AutomationRunStatus.Queued)} 个排队";

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(ListStatus));
        OnPropertyChanged(nameof(ListDetail));
    }

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }
    partial void OnStatusKindChanged(AutomationRunStatus? value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ListStatusBrush));
    }
    partial void OnProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsScheduleInvalid));
    }
    partial void OnAvailabilityChanged(AutomationTaskAvailability value)
    {
        OnPropertyChanged(nameof(IsScheduleInvalid));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(AvailabilityText));
    }

    internal void Apply(AutomationTaskView view)
    {
        Title = view.Template?.Title ?? view.Task.TemplateId;
        Name = view.Task.DisplayName;
        Icon = AutomationIcons.Parse(view.Template?.Icon ?? "");
        ScheduleEnabled = view.Task.ScheduleEnabled;
        ManualEnabled = view.Task.ManualEnabled;
        Problem = view.Problem;
        Availability = view.Availability;
        Current = view.Current;
        CurrentRuns = view.CurrentRuns;
        AllowParallel = view.Task.AllowParallel;
        _hasPendingReview = view.HasPendingReview;
        LastRunId = view.Last?.RunId;
        Last = view.Last;
        OnPropertyChanged(nameof(HasPendingReview));
        IsActive = view.Current != null;
        ListSchedule = !CanExecute ? AvailabilityText
            : view.Task.TriggerSummary;
        ApplyTriggerBadges(view.Task.ManualEnabled, view.Task.ScheduleEnabled, view.Task.ScheduleSummary);
        Schedule = !CanExecute
            ? (Availability == AutomationTaskAvailability.TemplateLoadFailed || view.Problem == AvailabilityText
                ? view.Problem ?? AvailabilityText : $"{AvailabilityText} · {view.Problem}")
            : HasPendingReview ? "待审核 · 完成审核后才能再次执行"
            : !view.Task.ScheduleEnabled ? view.Task.TriggerSummary
            : view.NextRun is { } next ? $"{view.Task.TriggerSummary} · 下次 {FormatNext(TimeZoneInfo.ConvertTime(new DateTimeOffset(next), view.Task.Trigger.Zone).DateTime)}{(view.NextRunFromServer ? " · 由服务器下发" : "")}"
            : view.Task.TriggerSummary;
        if (_flash != null) { UpdateCard(); return; }
        var run = CurrentRuns.FirstOrDefault(r => r.Status == AutomationRunStatus.Running) ?? view.Current ?? view.Last;
        StatusKind = run?.Status;
        var running = view.Current?.Steps.Where(s => s.State == AutomationStepState.Running).ToArray() ?? [];
        Progress = running.Length == 1 && running[0].HasProgress ? running[0].Progress : null;
        Status = run switch
        {
            null => view.Problem ?? "尚未运行",
            { Status: AutomationRunStatus.Queued } => "排队中 · 等待资源空闲",
            { Status: AutomationRunStatus.Running } => "运行中 · " + (running.Length == 0 ? "准备中"
                : string.Join("、", running.Select(s => s.Title))),
            { Status: AutomationRunStatus.AwaitingReview } => "待审核 · " + AutomationPalette.Preview(run.Summary),
            { } r => r.Status == AutomationRunStatus.Succeeded
                ? $"{AutomationPalette.StatusText(r.Status)} · {FormatWhen(r.EndedAt ?? r.QueuedAt)}"
                : $"{AutomationPalette.StatusText(r.Status)} · {FormatWhen(r.EndedAt ?? r.QueuedAt)} · {AutomationPalette.Preview(r.Summary)}"
        };
        if (CurrentRuns.Count > 1) Status = ActiveStatus;
        OnPropertyChanged(nameof(ListStatus));
        OnPropertyChanged(nameof(ListDetail));
        UpdateCard();
    }

    /// <summary>Mirrors the draft form into its list row while the user edits.</summary>
    internal void ApplyDraft(AutomationEditorViewModel editor)
    {
        Title = editor.TemplateTitle;
        Name = string.IsNullOrWhiteSpace(editor.Name) ? "（未命名）" : editor.Name.Trim();
        Icon = editor.Icon;
        ScheduleEnabled = editor.ScheduleEnabled;
        ManualEnabled = editor.ManualEnabled;
        Schedule = editor.TriggerSummary;
        ListSchedule = editor.TriggerSummary;
        ApplyTriggerBadges(editor.ManualEnabled, editor.ScheduleEnabled, editor.ScheduleSummary);
        Problem = null;
        StatusKind = null;
        Progress = null;
        Status = editor.HasViolations && editor.Violations.Any(v => v.IsError) ? "保存检查未通过" : "尚未保存";
        OnPropertyChanged(nameof(ListDetail));
        UpdateCard();
    }

    /// <summary>
    /// 卡片徽标只表达「额外信息」：手动是默认能力不画徽标；定时画中性徽标；
    /// 不可用时由问题徽标替代触发器（未加载 / 校验未通过为暖色，加载失败 / 已失效为红色）。
    /// </summary>
    private void ApplyTriggerBadges(bool manual, bool scheduled, string schedule)
    {
        var badges = new List<AutomationTriggerBadge>();
        if (!CanExecute)
        {
            var danger = Availability is AutomationTaskAvailability.TemplateLoadFailed or AutomationTaskAvailability.InvalidTask;
            badges.Add(new(AvailabilityText,
                danger ? FluentIcons.Common.Icon.ErrorCircle
                : Availability == AutomationTaskAvailability.ValidationFailed ? FluentIcons.Common.Icon.Warning
                : FluentIcons.Common.Icon.Info,
                danger ? AutomationBadgeTone.Danger : AutomationBadgeTone.Warning));
        }
        else
        {
            if (scheduled) badges.Add(new(schedule, FluentIcons.Common.Icon.Clock));
            if (!manual && !scheduled) badges.Add(new("未启用触发器", FluentIcons.Common.Icon.Info));
        }
        if (!TriggerBadges.SequenceEqual(badges)) TriggerBadges = badges;
    }

    /// <summary>卡片右上角状态 + 第二行时间：安静态（尚未运行 / 成功）只有圆点，关注态用胶囊；不可用时交给问题徽标。</summary>
    private void UpdateCard()
    {
        OnPropertyChanged(nameof(IsUnavailable));
        if (_flash != null)
        {
            CardStatus = Status;
            CardTone = AutomationCardTone.Failed;
            return;
        }
        if (IsDraft)
        {
            CardStatus = Status;
            CardTone = AutomationCardTone.Idle;
            CardTime = "";
            return;
        }
        string text;
        AutomationCardTone tone;
        string time;
        if (IsActive && (CurrentRuns.FirstOrDefault(r => r.Status == AutomationRunStatus.Running) ?? Current) is { } active)
        {
            tone = ToneOf(active.Status);
            text = CurrentRuns.Count > 1
                ? $"{CurrentRuns.Count(r => r.Status == AutomationRunStatus.Running)} 运行 · {CurrentRuns.Count(r => r.Status == AutomationRunStatus.Queued)} 排队"
                : AutomationPalette.StatusText(active.Status) + (active.Status == AutomationRunStatus.Running && Progress is { } p ? $" {p * 100:F0}%" : "");
            time = FormatWhen(active.StartedAt ?? active.QueuedAt);
        }
        else if (Last is { } last)
        {
            tone = ToneOf(last.Status);
            text = AutomationPalette.StatusText(last.Status);
            time = FormatWhen(last.EndedAt ?? last.QueuedAt);
        }
        else
        {
            tone = AutomationCardTone.Idle;
            text = "尚未运行";
            time = "";
        }
        if (HasPendingReview && !IsActive) { tone = AutomationCardTone.Review; text = "待审核"; }
        if (!CanExecute) tone = AutomationCardTone.None;
        CardStatus = text;
        CardTone = tone;
        CardTime = time;
    }

    private static AutomationCardTone ToneOf(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Queued => AutomationCardTone.Queued,
        AutomationRunStatus.Running => AutomationCardTone.Running,
        AutomationRunStatus.AwaitingReview => AutomationCardTone.Review,
        AutomationRunStatus.Succeeded => AutomationCardTone.Succeeded,
        AutomationRunStatus.Failed => AutomationCardTone.Failed,
        _ => AutomationCardTone.Cancelled
    };

    /// <summary>Shows a transient message in the status line (e.g. why 「立即运行」 did nothing).</summary>
    internal void Flash(string message)
    {
        _flash?.Cancel();
        var flash = _flash = new CancellationTokenSource();
        Status = message;
        StatusKind = AutomationRunStatus.Failed;
        UpdateCard();
        _ = Task.Delay(TimeSpan.FromSeconds(4), flash.Token).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_flash, flash)) _flash = null;
        }), TaskScheduler.Default);
    }

    internal static string FormatWhen(DateTimeOffset value)
    {
        var local = value.LocalDateTime;
        return local.Date == DateTime.Today ? local.ToString("HH:mm")
            : local.Date == DateTime.Today.AddDays(-1) ? "昨天 " + local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
    }

    private static string FormatNext(DateTime next) =>
        next.Date == DateTime.Today ? "今天 " + next.ToString("HH:mm")
        : next.Date == DateTime.Today.AddDays(1) ? "明天 " + next.ToString("HH:mm")
        : next.ToString("MM-dd HH:mm");
}

public enum AutomationBadgeTone { Neutral, Warning, Danger }

/// <summary>卡片状态外观：None 表示不显示状态（不可用任务由问题徽标说明）。</summary>
public enum AutomationCardTone { None, Idle, Queued, Running, Review, Succeeded, Failed, Cancelled }

public sealed record AutomationTriggerBadge(string Text, FluentIcons.Common.Icon Icon, AutomationBadgeTone Tone = AutomationBadgeTone.Neutral)
{
    public bool IsNeutral => Tone == AutomationBadgeTone.Neutral;
    public bool IsWarning => Tone == AutomationBadgeTone.Warning;
    public bool IsDanger => Tone == AutomationBadgeTone.Danger;
}

/// <summary>One run in the history list; selected run shows its step timeline.</summary>
public sealed partial class AutomationRunItemViewModel : ObservableObject
{
    private AutomationRunRecord _historySummary;
    internal bool DetailsLoaded { get; private set; }
    internal AutomationRunRecord HistorySummary => _historySummary;

    public AutomationRunItemViewModel(AutomationRunRecord record)
    {
        _historySummary = record;
        Apply(record);
    }

    internal void ApplySummary(AutomationRunRecord record)
    {
        _historySummary = record;
        if (!DetailsLoaded) Apply(record);
    }

    internal void LoadDetails(AutomationRunRecord record)
    {
        DetailsLoaded = true;
        Apply(record);
    }

    internal void UnloadDetails()
    {
        DetailsLoaded = false;
        Apply(_historySummary);
    }

    public string RunId { get; private set; } = "";
    internal AutomationRunRecord Record { get; private set; } = new();
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warning = "";
    public bool HasWarning => Warning.Length > 0;
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    [ObservableProperty] private string _report = "";
    public string SummaryPreview => AutomationPalette.Preview(Summary);
    public bool HasReport => !string.IsNullOrWhiteSpace(Report);
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(SummaryPreview));
    partial void OnReportChanged(string value) => OnPropertyChanged(nameof(HasReport));
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private AutomationRunStatus _status;
    /// <summary>History column: how the run started; a derived run says 「继续」 or 「重跑」.</summary>
    [ObservableProperty] private string _triggerText = "";
    /// <summary>Run summary: who triggered it. Derived runs are always started manually.</summary>
    [ObservableProperty] private string _triggerSourceText = "";
    [ObservableProperty] private AutomationReview? _review;
    [ObservableProperty] private string? _artifactPath;
    public bool HasReview => Review != null;
    public bool HasArtifact => Record.Artifacts.Count > 0 || !string.IsNullOrEmpty(ArtifactPath);
    public IReadOnlyList<Heron.Pregel.Tooling.ArtifactRecord> RegisteredArtifacts => Record.Artifacts;
    public string ReviewCaption => Review == null ? "" : $"第 {Review.Revision} 版 · {Review.Detail}";
    public string ReviewContent => Review?.Content ?? "";
    public IReadOnlyList<AutomationReviewMessage> ReviewConversation => Review?.Conversation ?? [];
    public bool HasReviewConversation => ReviewConversation.Count > 0;
    public string ReviewConversationCaption => $"展开对话 · {ReviewConversation.Count(message => message.Role == "user")} 轮";
    public string ReviewContinuationDetail => Review?.ContinuationDetail ?? "";
    public bool HasReviewContinuationDetail => ReviewContinuationDetail.Length > 0;
    partial void OnReviewChanged(AutomationReview? value)
    {
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(ReviewCaption));
        OnPropertyChanged(nameof(ReviewContent));
        OnPropertyChanged(nameof(ReviewConversation));
        OnPropertyChanged(nameof(HasReviewConversation));
        OnPropertyChanged(nameof(ReviewConversationCaption));
        OnPropertyChanged(nameof(ReviewContinuationDetail));
        OnPropertyChanged(nameof(HasReviewContinuationDetail));
    }
    partial void OnArtifactPathChanged(string? value) => OnPropertyChanged(nameof(HasArtifact));
    public ObservableCollection<AutomationStepItemViewModel> Steps { get; } = [];
    public IBrush StatusBackground => AutomationPalette.StatusBackground(Status);
    public bool HasDuration => Duration.Length > 0;
    partial void OnDurationChanged(string value) => OnPropertyChanged(nameof(HasDuration));
    public bool HasEvidence => RunId.Length > 0;
    public bool HasSteps => Steps.Count > 0;
    public IBrush StatusBrush => AutomationPalette.Status(Status, false);
    public string StatusText => AutomationPalette.StatusText(Status);

    internal void RefreshTheme()
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusBackground));
        foreach (var step in Steps) step.RefreshTheme();
    }

    partial void OnStatusChanged(AutomationRunStatus value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBackground));
    }

    internal void Apply(AutomationRunRecord record)
    {
        Record = record;
        NotifyLaunchParameters();
        RunId = record.RunId;
        Status = record.Status;
        TriggerSourceText = record.Trigger switch { "schedule" => "定时", "tray" => "托盘", "cli" => "CLI", _ => "手动" };
        TriggerText = record.SourceRunId == null ? TriggerSourceText
            : record.ReexecutionMode == nameof(Heron.Pregel.PregelReexecutionMode.Continue) ? "继续" : "重跑";
        Title = AutomationTaskItemViewModel.FormatWhen(record.StartedAt ?? record.QueuedAt);
        Summary = record.Summary;
        Warning = record.Warning;
        var reportParts = new List<string> { record.Summary };
        AppendErrors(record.Steps);
        Report = string.Join("\n\n", reportParts.Where(part => !string.IsNullOrWhiteSpace(part)));

        void AppendErrors(IEnumerable<AutomationStepRecord> steps)
        {
            foreach (var step in steps)
            {
                if (!string.IsNullOrWhiteSpace(step.Error)
                    && !reportParts.Any(part => part.Contains(step.Error, StringComparison.Ordinal)))
                    reportParts.Add($"{step.Title}\n{step.Error}");
                AppendErrors(step.Children);
            }
        }

        Review = record.Review;
        ArtifactPath = record.ArtifactPath;
        OnPropertyChanged(nameof(RegisteredArtifacts));
        OnPropertyChanged(nameof(HasArtifact));
        Duration = record.Status == AutomationRunStatus.AwaitingReview ? "" : record.StartedAt is { } start
            ? FormatDuration((record.EndedAt ?? DateTimeOffset.Now) - start)
            : "";
        OnPropertyChanged(nameof(HasEvidence));

        var orderedSteps = AutomationStepTopology.InDependencyOrder(record.Steps);
        if (Steps.Count != orderedSteps.Count || Steps.Where((step, index) =>
                step.Key != orderedSteps[index].Key
                || (step.DependsOn == null) != (orderedSteps[index].DependsOn == null)
                || !(step.DependsOn ?? []).SequenceEqual(orderedSteps[index].DependsOn ?? [])).Any())
            Steps.Clear();
        for (var i = 0; i < orderedSteps.Count; i++)
        {
            if (i < Steps.Count) Steps[i].Apply(orderedSteps[i]);
            else Steps.Add(new AutomationStepItemViewModel(orderedSteps[i]));
        }
        while (Steps.Count > orderedSteps.Count) Steps.RemoveAt(Steps.Count - 1);
        if (Steps.Count > 0) Steps[^1].IsLast = true;
        OnPropertyChanged(nameof(HasSteps));
    }

    internal static string FormatDuration(TimeSpan value) =>
        value.TotalSeconds < 1 ? "<1 秒"
        : value.TotalMinutes < 1 ? $"{value.Seconds} 秒"
        : value.TotalHours < 1 ? $"{(int)value.TotalMinutes} 分 {value.Seconds:00} 秒"
        : $"{(int)value.TotalHours} 小时 {value.Minutes:00} 分";
}

/// <summary>One graph node: state icon + single-line title + single-line「状态 · 补充信息」; styles live in AutomationGraph.axaml.</summary>
public sealed partial class AutomationStepItemViewModel : ObservableObject, Heron.ClientControls.Graphs.IGraphItem
{
    internal IReadOnlyList<string> NodeIds { get; private set; } = [];
    private readonly bool _isPlan;
    /// <summary>Set by <see cref="AutomationReexecutionViewModel"/>: this node can be a restore point of the selected run.</summary>
    [ObservableProperty] private bool _canRerun;
    /// <summary>Set by <see cref="AutomationReexecutionViewModel"/>: this failed or cancelled node offers 「重试」 (continue from the failure).</summary>
    [ObservableProperty] private bool _canRetry;
    /// <summary>Plan preview only: a top-level step or a stage member that 「运行所选」 can tick. Finally and recovery always run.</summary>
    [ObservableProperty] private bool _canSelectForRun;
    [ObservableProperty] private bool _isRunSelected;

    /// <param name="isPlan">任务编排预览：只表达结构，不显示「等待」等运行状态。</param>
    public AutomationStepItemViewModel(AutomationStepRecord record, bool isPlan = false)
    {
        _isPlan = isPlan;
        Apply(record);
    }
    public string Key { get; private set; } = "";
    public IReadOnlyList<string>? DependsOn { get; private set; }
    public IReadOnlyList<string> ConditionalSources { get; private set; } = [];
    // Finally follows the last visible body step in the diagram; execution keeps its own routing.
    IReadOnlyList<string>? IGraphItem.DependsOn => IsFinally
        ? ConditionalSources.Count > 0 ? [ConditionalSources[^1]] : null
        : DependsOn;
    IReadOnlyList<string> IGraphItem.ConditionalSources => IsFinally ? [] : ConditionalSources;
    public bool IsStage { get; private set; }
    public bool IsRecovery { get; private set; }
    public bool IsFinally { get; private set; }
    public bool HasFinalizationCondition => IsRecovery || IsFinally;
    public bool HasStageHeader => IsStage || IsRecovery;
    public bool CanOpenSubgraph => HasChildren && !IsStage;
    public string StageTitle => IsRecovery ? "失败恢复" : Title;
    public string StageCondition => IsFinally ? "完成、失败或取消后进入" : IsRecovery ? "失败或取消时进入" : "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private AutomationStepState _state;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private bool _isLast;
    [ObservableProperty] private bool _isReused;
    [ObservableProperty] private int _attempt;
    [ObservableProperty] private string? _retryError;
    /// <summary>Running with a failed previous attempt: waiting for or executing a retry.</summary>
    public bool IsRetrying => IsRunning && !string.IsNullOrEmpty(RetryError);

    public ObservableCollection<AutomationStepItemViewModel> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;

    internal static AutomationStepRecord Preview(AutomationStep step) => new()
    {
        Key = step.Key, Title = step.Title, DependsOn = step.DependsOn, IsStage = step.IsStage,
        IsRecovery = step.IsRecovery, IsFinally = step.IsFinally, RecoveryFrom = step.RecoveryFrom,
        Children = step.Children.Select(Preview).ToArray()
    };

    public bool IsPlan => _isPlan;
    public bool IsPending => !_isPlan && State is AutomationStepState.Pending or AutomationStepState.Queued;
    public bool IsRunning => !_isPlan && State == AutomationStepState.Running;
    public bool IsReview => !_isPlan && State == AutomationStepState.AwaitingReview;
    public bool IsSucceeded => !_isPlan && State == AutomationStepState.Succeeded;
    public bool IsFailed => !_isPlan && State == AutomationStepState.Failed;
    public bool IsSkipped => !_isPlan && State is AutomationStepState.Skipped or AutomationStepState.NotRun;
    public bool IsCancelled => !_isPlan && State == AutomationStepState.Cancelled;
    /// <summary>HumanGate 节点本身（不是所在 Stage）在右侧提供「审核」入口。</summary>
    public bool IsReviewNode => IsReview && !HasStageHeader;

    public FluentIcons.Common.Icon StateIcon => _isPlan ? FluentIcons.Common.Icon.Circle
        : IsReused ? FluentIcons.Common.Icon.History : State switch
    {
        AutomationStepState.Succeeded => FluentIcons.Common.Icon.CheckmarkCircle,
        AutomationStepState.Failed => FluentIcons.Common.Icon.DismissCircle,
        AutomationStepState.Running => FluentIcons.Common.Icon.ArrowSync,
        AutomationStepState.AwaitingReview => FluentIcons.Common.Icon.PersonClock,
        AutomationStepState.Skipped or AutomationStepState.NotRun => FluentIcons.Common.Icon.SubtractCircle,
        AutomationStepState.Cancelled => FluentIcons.Common.Icon.RecordStop,
        _ => FluentIcons.Common.Icon.Clock
    };

    public string StateText => State switch
    {
        AutomationStepState.Pending => "等待",
        AutomationStepState.Queued => "排队中",
        AutomationStepState.NotRun => "未执行",
        AutomationStepState.Skipped => "已跳过",
        AutomationStepState.Running => ShowProgress && !IsProgressIndeterminate ? $"{Progress:F0}%" : "进行中",
        AutomationStepState.Cancelled => "已取消",
        AutomationStepState.AwaitingReview => "待审核",
        _ => Duration
    };

    /// <summary>图上显示的状态短语：编排预览为空，失败带耗时，成功无耗时时写「已完成」。</summary>
    public string StatusLabel => _isPlan ? "" : IsReused ? State switch
    {
        AutomationStepState.Skipped => "已跳过（复用）",
        AutomationStepState.Failed => "失败（复用）",
        _ => "复用原结果"
    } : State switch
    {
        AutomationStepState.Running => (ShowProgress && !IsProgressIndeterminate ? $"进行中 {Progress:F0}%" : "进行中")
            + (Attempt > 1 ? $" · 第 {Attempt} 次尝试" : "") + (QueuedCount > 0 ? $" · {QueuedCount} 项排队" : ""),
        AutomationStepState.Queued => QueuedCount > 1 ? $"排队中 · {QueuedCount} 项" : "排队中",
        AutomationStepState.Failed => (Duration.Length > 0 ? $"失败 · {Duration}" : "失败") + (Attempt > 1 ? $" · 已尝试 {Attempt} 次" : ""),
        AutomationStepState.Succeeded => (Duration.Length > 0 ? Duration : "已完成") + (Attempt > 1 ? $" · 第 {Attempt} 次成功" : ""),
        _ => StateText
    };

    /// <summary>节点第二行：状态 + 补充信息（进度说明、跳过原因、错误首行），单行省略，完整内容看 Tooltip。</summary>
    public string MetaLine => string.Join(" · ", new[] { StatusLabel, HasDetail ? Detail : "", HasError ? ErrorPreview : "" }
        .Where(part => part.Length > 0));
    public bool HasMeta => MetaLine.Length > 0;
    public string ToolTipText => string.Join("\n", new[]
        {
            Title, HasDetail ? Detail : "", Error ?? "",
            string.IsNullOrEmpty(RetryError) ? "" : "上次尝试失败：" + RetryError,
            IsReused ? "本次未执行，结果来自原运行" : ""
        }.Where(part => part.Length > 0));

    private int ParallelCount => Children.Where((child, index) => child.DependsOn is { Count: 0 } || child.DependsOn == null && index == 0).Count();
    public bool IsParallel => IsStage && ParallelCount > 1;
    public string ParallelCaption => $"{ParallelCount} 路并行";
    public bool HasStageNote => IsParallel || HasFinalizationCondition;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public string ErrorPreview => AutomationPalette.Preview(Error).ReplaceLineEndings(" ");
    public bool HasDetail => Detail.Length > 0 && (State is AutomationStepState.Running or AutomationStepState.Queued
        or AutomationStepState.Skipped or AutomationStepState.AwaitingReview);
    private int QueuedCount => Children.Sum(child => child.State == AutomationStepState.Queued && !child.IsStage
        ? 1 : child.QueuedCount);

    internal void RefreshTheme()
    {
        foreach (var child in Children) child.RefreshTheme();
    }

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(ToolTipText));
    }

    partial void OnStateChanged(AutomationStepState value)
    {
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsReviewNode));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(IsRetrying));
        NotifyDisplay();
    }

    partial void OnIsReusedChanged(bool value)
    {
        OnPropertyChanged(nameof(StateIcon));
        NotifyDisplay();
    }
    partial void OnAttemptChanged(int value) => NotifyDisplay();
    partial void OnRetryErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(IsRetrying));
        NotifyDisplay();
    }

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorPreview));
        NotifyDisplay();
    }
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(ToolTipText));
    partial void OnDetailChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetail));
        NotifyDisplay();
    }
    partial void OnDurationChanged(string value) => NotifyDisplay();
    partial void OnProgressChanged(double value) => NotifyDisplay();
    partial void OnShowProgressChanged(bool value) => NotifyDisplay();
    partial void OnIsProgressIndeterminateChanged(bool value) => NotifyDisplay();

    internal void Apply(AutomationStepRecord record)
    {
        NodeIds = record.NodeIds;
        var orderedChildren = AutomationStepTopology.InDependencyOrder(record.Children);
        if (!Children.Select(c => c.Key).SequenceEqual(orderedChildren.Select(c => c.Key))) Children.Clear();
        for (var i = 0; i < orderedChildren.Count; i++)
        {
            if (i < Children.Count) Children[i].Apply(orderedChildren[i]);
            else Children.Add(new AutomationStepItemViewModel(orderedChildren[i], _isPlan));
        }
        OnPropertyChanged(nameof(HasChildren));
        Key = record.Key;
        DependsOn = record.DependsOn;
        ConditionalSources = record.RecoveryFrom;
        IsStage = record.IsStage;
        IsRecovery = record.IsRecovery;
        IsFinally = record.IsFinally;
        Title = record.Title;
        State = record.State;
        Detail = record.Detail;
        Error = record.Error;
        IsReused = record.IsReused;
        Attempt = record.Attempt;
        RetryError = record.RetryError;
        ShowProgress = record.HasProgress && record.State == AutomationStepState.Running;
        IsProgressIndeterminate = record.Progress == null;
        Progress = (record.Progress ?? 0) * 100;
        Duration = record.StartedAt is { } start && record.EndedAt is { } end
            ? AutomationRunItemViewModel.FormatDuration(end - start)
            : "";
        OnPropertyChanged(nameof(HasStageHeader));
        OnPropertyChanged(nameof(CanOpenSubgraph));
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(StageCondition));
        OnPropertyChanged(nameof(IsStage));
        OnPropertyChanged(nameof(IsRecovery));
        OnPropertyChanged(nameof(IsFinally));
        OnPropertyChanged(nameof(HasFinalizationCondition));
        OnPropertyChanged(nameof(IsReviewNode));
        OnPropertyChanged(nameof(IsParallel));
        OnPropertyChanged(nameof(ParallelCaption));
        OnPropertyChanged(nameof(HasStageNote));
        NotifyDisplay();
    }
}

/// <summary>Uniform settings form for one task; fields come from the template.</summary>
public sealed partial class AutomationEditorViewModel : ObservableObject
{
    private readonly IAutomationTemplate _template;
    private readonly AutomationTask? _original;
    private readonly Action<AutomationTask> _save;
    private readonly Action _changed;
    private readonly Func<AutomationTask, IReadOnlyList<AutomationRuleViolation>> _check;
    private readonly AutomationTask _draftIdentity;
    private AutomationTask? _saved;
    private bool _loadingChoices;
    private string _ifError = "";
    private CancellationTokenSource? _choicesCancellation;

    /// <param name="task">Saved task being edited; null for a new draft.</param>
    /// <param name="check">Host rule check (name, time, parameters, business rules) for the task as it would be saved.</param>
    /// <param name="suggestedName">New drafts only: default name (「每日自动拉新」「每日自动拉新-2」…).</param>
    /// <param name="seed">New drafts only: settings to start from (「复制为新任务」).</param>
    public AutomationEditorViewModel(IAutomationTemplate template, AutomationTask? task, Action<AutomationTask> save, Action changed,
        Func<AutomationTask, IReadOnlyList<AutomationRuleViolation>>? check = null, string? suggestedName = null,
        AutomationTask? seed = null, IReadOnlyDictionary<string, string>? parameters = null)
    {
        _template = template;
        _original = task;
        _save = save;
        _changed = changed;
        _check = check ?? (_ => []);
        _draftIdentity = new AutomationTask { TemplateId = template.Id, ScriptInputPath = seed?.ScriptInputPath,
            TriggerSourcePath = seed?.TriggerSourcePath, TriggerSourceVersion = seed?.TriggerSourceVersion };
        var source = task ?? seed;
        _name = task?.DisplayName ?? suggestedName ?? template.Title;
        InitializeSchedules(source?.Schedules ?? [new AutomationTrigger()]);
        _scheduleEnabled = source?.ScheduleEnabled ?? false;
        _manualEnabled = source?.ManualEnabled ?? true;
        _notifyOnSuccess = source?.NotifyOnSuccess ?? true;
        _allowParallel = source?.AllowParallel ?? false;
        Fields = template.Fields.Select(field => new AutomationFieldViewModel(field,
            parameters?.GetValueOrDefault(field.Key) ?? source?.Parameter(field.Key, field.DefaultValue) ?? field.DefaultValue, () => ParameterChanged(field.Key))).ToArray();
        ParameterGroups = Fields.GroupBy(field => field.Field.Group?.Trim() ?? "")
            .Select(group => new AutomationParameterGroupViewModel(group.Key, group.ToArray())).ToArray();
        UpdateFieldVisibility();
        _isDirty = task == null;
        _saved = task == null ? null : BuildDraft();
        _ = LoadFieldChoicesAsync();
    }

    public string TemplateTitle => _template.Title;
    [ObservableProperty] private string _cloudTaskName = "服务器尚未确认此流程的登记信息";
    [ObservableProperty] private string _cloudTaskId = "";
    internal void SetCloudTask(Heron.AutomationTenants.RemoteAutomationTaskDefinition? task)
    {
        CloudTaskName = task?.Name ?? "服务器尚未确认此流程的登记信息";
        CloudTaskId = task?.Id ?? "";
    }
    public string TemplateId => _template.Id;
    internal bool UsesTemplate(IAutomationTemplate template) => ReferenceEquals(_template, template);
    internal bool MatchesTriggers(AutomationTask task) => _original is not { } original
        || original.SameSchedules(task) && original.TriggerSourcePath == task.TriggerSourcePath
            && original.TriggerSourceVersion == task.TriggerSourceVersion;
    public string Description => _template.Description;
    public FluentIcons.Common.Icon Icon => AutomationIcons.Parse(_template.Icon);
    public bool IsNew => _original == null;
    public string Heading => IsNew ? $"新建 · {_template.Title}" : _template.Title;
    public IReadOnlyList<AutomationFieldViewModel> Fields { get; }
    public IEnumerable<AutomationFieldViewModel> VisibleFields => Fields.Where(field => field.IsVisible);
    public IReadOnlyList<AutomationParameterGroupViewModel> ParameterGroups { get; }
    public IEnumerable<AutomationParameterGroupViewModel> VisibleParameterGroups => ParameterGroups.Where(group => group.HasVisibleFields);

    internal void RefreshGlobalSettings()
    {
        foreach (var field in Fields) field.RefreshGlobalValue();
        if (UpdateFieldVisibility()) _ = LoadFieldChoicesAsync();
        OnPropertyChanged(nameof(ParameterPreview));
        OnPropertyChanged(nameof(HasParameterPreview));
        OnPropertyChanged(nameof(PlanSteps));
        OnPropertyChanged(nameof(PlanPreview));
        ShowViolations([]);
        _changed();
    }

    public string? ParameterPreview
    {
        get
        {
            try { return _template.RenderParameters(BuildDraft()); }
            catch (Exception error) { return $"参数预览失败：{error.Message}"; }
        }
    }
    public bool HasParameterPreview => ParameterPreview != null;

    /// <summary>Result of the last save attempt: errors (blocked) and warnings (saved anyway).</summary>
    public ObservableCollection<AutomationViolationItem> Violations { get; } = [];
    public bool HasViolations => Violations.Count > 0;

    /// <summary>The steps this task will run with the current (possibly unsaved) options.</summary>
    public IReadOnlyList<AutomationStepChip> PlanSteps
    {
        get
        {
            try
            {
                var steps = _template.PlanSteps(BuildDraft());
                return steps.Select((s, i) => new AutomationStepChip(i + 1, s.Title, i == steps.Count - 1, s.Key, s.DependsOn, s.Children)).ToArray();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    /// <summary>Same plan as graph nodes (all 「等待」), drawn in the run area before the task has any run.</summary>
    [ObservableProperty] private string _planPreviewError = "";
    public bool HasPlanPreviewError => PlanPreviewError.Length > 0;
    partial void OnPlanPreviewErrorChanged(string value) => OnPropertyChanged(nameof(HasPlanPreviewError));

    internal void RefreshPlanPreview() => OnPropertyChanged(nameof(PlanPreview));

    public IReadOnlyList<AutomationStepItemViewModel> PlanPreview
    {
        get
        {
            try
            {
                var steps = _template.PlanSteps(BuildDraft())
                    .Select(s => new AutomationStepItemViewModel(AutomationStepItemViewModel.Preview(s), isPlan: true)).ToArray();
                foreach (var step in steps)
                {
                    step.CanSelectForRun = step is { IsFinally: false, IsRecovery: false };
                    if (step.IsStage)
                        foreach (var child in step.Children) child.CanSelectForRun = child is { IsFinally: false, IsRecovery: false };
                }
                PlanPreviewError = "";
                return steps;
            }
            catch (Exception ex)
            {
                var message = "流程预览生成失败：" + ex.Message;
                if (PlanPreviewError != message)
                    Serilog.Log.Warning(ex, "[Automation] plan preview failed for {TemplateId}", _template.Id);
                PlanPreviewError = message;
                return [];
            }
        }
    }

    internal void RefreshTheme()
    {
        // PlanPreview 每次读取都会创建节点；通知绑定重取，才能更新屏幕上的预览实例。
        OnPropertyChanged(nameof(PlanPreview));
        foreach (var violation in Violations) violation.RefreshTheme();
    }

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private bool _manualEnabled = true;
    [ObservableProperty] private bool _notifyOnSuccess;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnNameChanged(string value) => MarkDirty();
    partial void OnScheduleEnabledChanged(bool value)
    {
        IsScheduleDetailsExpanded = value;
        ScheduleChanged();
    }

    partial void OnManualEnabledChanged(bool value) => ScheduleChanged();

    partial void OnNotifyOnSuccessChanged(bool value) => MarkDirty();
    [ObservableProperty] private bool _allowParallel;
    partial void OnAllowParallelChanged(bool value) => MarkDirty();
    partial void OnIsDirtyChanged(bool value) => _changed();

    [RelayCommand]
    private void Save() => TrySave();

    /// <summary>
    /// Runs every rule, then saves when none is an error. Errors stay listed and block; warnings stay listed after
    /// the save so the user sees what to keep in mind. Returns false when not saved.
    /// </summary>
    internal bool TrySave()
    {
        if (_ifError.Length > 0) { Error = _ifError; return false; }
        // 只物化真正的存储默认值（如拉包目录）；「全局设置」的值不写进任务，留空即沿用全局。
        foreach (var field in Fields.Where(f => f.IsVisible && !f.Field.IsReadOnly && string.IsNullOrWhiteSpace(f.Value) && f.Field.EmptyValue != null))
            field.Value = field.Field.EmptyValue!;
        var task = BuildDraft();
        IReadOnlyList<AutomationRuleViolation> violations;
        try { violations = _check(task); }
        catch (Exception ex) { violations = [AutomationRuleViolation.Error(AutomationRules.RuleFailed, "检查任务配置时出错：" + ex.Message)]; }
        ShowViolations(violations);
        var errors = violations.Count(v => v.Severity == AutomationRuleSeverity.Error);
        if (errors > 0)
        {
            Error = $"有 {errors} 项需要修改";
            return false;
        }
        Error = "";
        try
        {
            _save(task);
            _saved = task;
            IsDirty = false;
            return true;
        }
        catch (AutomationValidationException ex)
        {
            ShowViolations(ex.Violations);
            Error = $"有 {ex.Violations.Count} 项需要修改";
            return false;
        }
        catch (Exception ex)
        {
            Error = "保存失败：" + ex.Message;
            return false;
        }
    }

    internal void ShowViolations(IEnumerable<AutomationRuleViolation> violations)
    {
        Violations.Clear();
        foreach (var violation in violations) Violations.Add(new AutomationViolationItem(violation));
        OnPropertyChanged(nameof(HasViolations));
    }

    private AutomationTask BuildDraft() => (_original ?? _draftIdentity).WithSchedules(BuildTriggers()) with
    {
        Name = (Name ?? "").Trim(),
        ScheduleEnabled = ScheduleEnabled,
        ManualEnabled = ManualEnabled,
        NotifyOnSuccess = NotifyOnSuccess,
        AllowParallel = AllowParallel,
        // 留空的参数不保存：读取时回落到全局设置与输入默认值，否则空串会挡住全局值。
        Parameters = Fields.Where(f => !f.Field.IsReadOnly && !string.IsNullOrWhiteSpace(f.Value))
            .ToDictionary(f => f.Key, f => f.Value)
    };

    private bool UpdateFieldVisibility()
    {
        // Include read-only fields and empty defaults in the predicate input, while keeping storage rules unchanged.
        var draft = BuildDraft() with { Parameters = Fields.ToDictionary(field => field.Key, field => field.Value) };
        var previousError = _ifError;
        _ifError = "";
        var needsChoices = false;
        var visibilityChanged = false;
        foreach (var field in Fields)
        {
            var visible = true;
            try { visible = field.Field.If?.Invoke(draft) ?? true; }
            catch (Exception error)
            {
                // Keep the field accessible while its condition cannot be evaluated, so the draft can be corrected.
                if (_ifError.Length == 0) _ifError = $"「{field.Label}」的 If 条件计算失败：{error.Message}";
            }
            needsChoices |= visible && !field.IsVisible && field.HasDynamicChoices;
            visibilityChanged |= visible != field.IsVisible;
            field.IsVisible = visible;
        }
        if (visibilityChanged)
        {
            foreach (var group in ParameterGroups) group.RefreshVisibility();
            OnPropertyChanged(nameof(VisibleFields));
            OnPropertyChanged(nameof(VisibleParameterGroups));
        }
        if (_ifError.Length > 0) Error = _ifError;
        else if (Error == previousError) Error = "";
        return needsChoices;
    }

    private void ParameterChanged(string key)
    {
        var needsChoices = UpdateFieldVisibility();
        if (!_loadingChoices)
            foreach (var dependent in Fields.Where(f => f.Field.ChoiceDependencies?.Contains(key) == true))
                dependent.Value = "";
        MarkDirty();
        if (!_loadingChoices && (needsChoices || Fields.Any(f => f.Field.ChoiceDependencies?.Contains(key) == true)))
            _ = LoadFieldChoicesAsync();
    }

    private void MarkDirty()
    {
        RefreshDirty();
        // A result only describes the options it was computed for; drop it once they change.
        if (Violations.Count > 0) ShowViolations([]);
        OnPropertyChanged(nameof(PlanSteps));
        OnPropertyChanged(nameof(PlanPreview));
        OnPropertyChanged(nameof(ParameterPreview));
        OnPropertyChanged(nameof(HasParameterPreview));
        _changed();
    }

    private async Task LoadFieldChoicesAsync()
    {
        _choicesCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _choicesCancellation = cancellation;
        try
        {
            var loaded = new HashSet<AutomationFieldViewModel>();
            while (Fields.FirstOrDefault(f => f.IsVisible && f.HasDynamicChoices && !loaded.Contains(f)) is { } field)
            {
                loaded.Add(field);
                field.BeginChoicesLoad();
                try
                {
                    var choices = await _template.ListFieldChoicesAsync(BuildDraft(), field.Key, cancellation.Token);
                    if (cancellation.IsCancellationRequested) break;
                    _loadingChoices = true;
                    try { field.ApplyChoices(choices ?? []); }
                    finally { _loadingChoices = false; }
                }
                catch (OperationCanceledException) when (_choicesCancellation != cancellation) { break; }
                catch (Exception ex)
                {
                    if (_choicesCancellation == cancellation) field.FailChoicesLoad(
                        ex is OperationCanceledException ? "读取选项超时，请重新读取" : ex.Message);
                }
            }
        }
        finally
        {
            if (_choicesCancellation == cancellation) _choicesCancellation = null;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private Task RefreshFieldChoices() => LoadFieldChoicesAsync();

    internal void CancelFieldChoices() => _choicesCancellation?.Cancel();

    private void RefreshDirty()
    {
        // Compare saved values, not binding notifications or refreshed choice record identity.
        IsDirty = _saved == null
            || (Name ?? "").Trim() != _saved.Name
            || !AutomationSchedules.Same(BuildTriggers(), _saved.Schedules)
            || ScheduleEnabled != _saved.ScheduleEnabled || ManualEnabled != _saved.ManualEnabled || NotifyOnSuccess != _saved.NotifyOnSuccess || AllowParallel != _saved.AllowParallel
            || Fields.Where(f => !f.Field.IsReadOnly).Any(f => f.IsToggle
                ? f.BoolValue != _saved.Flag(f.Key)
                // Empty inputs are omitted when saving; clearing a saved override is still a change.
                : (string.IsNullOrWhiteSpace(f.Value) ? "" : f.Value) != _saved.Parameter(f.Key));
    }
}

/// <summary>One line of 「保存检查」.</summary>
public sealed class AutomationViolationItem(AutomationRuleViolation violation) : ObservableObject
{
    public AutomationRuleViolation Violation { get; } = violation;
    public string Message => Violation.Message;
    public bool IsError => Violation.Severity == AutomationRuleSeverity.Error;
    public string Glyph => IsError ? "✗" : "!";
    public IBrush Foreground => IsError ? AutomationPalette.Danger : AutomationPalette.Warning;
    public IBrush Background => IsError ? AutomationPalette.DangerSoft : AutomationPalette.WarningSoft;
    public string? ConflictTaskId => Violation.ConflictTaskId;
    public bool HasConflict => ConflictTaskId != null;

    internal void RefreshTheme()
    {
        OnPropertyChanged(nameof(Foreground));
        OnPropertyChanged(nameof(Background));
    }
}

public sealed class AutomationParameterGroupViewModel(string title, IReadOnlyList<AutomationFieldViewModel> fields) : ObservableObject
{
    public string Title { get; } = title;
    public bool HasTitle => Title.Length > 0;
    public IReadOnlyList<AutomationFieldViewModel> Fields { get; } = fields;
    public IEnumerable<AutomationFieldViewModel> VisibleFields => Fields.Where(field => field.IsVisible);
    public bool HasVisibleFields => Fields.Any(field => field.IsVisible);

    internal void RefreshVisibility()
    {
        OnPropertyChanged(nameof(VisibleFields));
        OnPropertyChanged(nameof(HasVisibleFields));
    }
}

public sealed partial class AutomationFieldViewModel : ObservableObject
{
    private readonly Action _changed;

    public AutomationFieldViewModel(AutomationField field, string value, Action changed)
    {
        Field = field;
        // 任务参数优先；留空时才用「全局设置」的值，该值只作占位，不写回任务。
        if (field.IsReadOnly) value = field.DefaultValue;
        _value = field.Kind == DisplayFieldKind.Folder && string.IsNullOrWhiteSpace(value)
            ? field.DefaultValue : value;
        _changed = changed;
        Choices = field.With ?? (field.Choices ?? []).Select(choice => new AutomationChoice(choice, choice)).ToArray();
        _selectedChoice = Choices.FirstOrDefault(choice => choice.Value == value);
        if (IsMultiChoice) RebuildMultiChoices();
    }

    public AutomationField Field { get; }
    public string Key => Field.Key;
    public string Label => Field.Label;
    public string? Hint => Field.GlobalValue is { Length: > 0 } global
        ? (Field.Hint is { Length: > 0 } hint ? hint + " " : "") + "留空沿用自动化「全局设置」：" + global
        : Field.Hint;
    /// <summary>Saved task value, or the 全局设置 value while the task value is empty.</summary>
    public string? Placeholder => Field.IsReadOnly ? Field.EmptyValue
        : Field.GlobalValue is { Length: > 0 } global ? global : Field.EmptyValue;
    [ObservableProperty] private bool _isVisible = true;
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    public bool IsToggle => Field.Kind == DisplayFieldKind.Toggle;
    public bool IsText => Field.Kind is DisplayFieldKind.Text or DisplayFieldKind.Folder;
    public bool IsFolder => Field.Kind == DisplayFieldKind.Folder;
    public bool IsChoice => Field.Kind == DisplayFieldKind.Choice;
    public bool IsMultiChoice => Field.Kind == DisplayFieldKind.MultiChoice;
    public bool IsMultiline => Field.Kind == DisplayFieldKind.Multiline;
    [ObservableProperty] private IReadOnlyList<AutomationChoice> _choices = [];
    [ObservableProperty] private AutomationChoice? _selectedChoice;
    [ObservableProperty] private bool _isLoadingChoices;
    [ObservableProperty] private string _choiceError = "";
    private bool _updatingChoices;
    public bool HasDynamicChoices => (IsChoice || IsMultiChoice) && !Field.IsReadOnly && Field.Choices == null && Field.With == null;
    public bool CanSelectChoice => !Field.IsReadOnly && !IsLoadingChoices
        && (IsMultiChoice || Choices.Count > 0 || ChoiceError.Length == 0);
    public string ChoicesStatus => IsLoadingChoices ? $"正在读取{Label}可选项…"
        : ChoiceError.Length > 0 && HasDynamicChoices ? $"可选项读取提示：{ChoiceError}。已填写的值仍由流程校验。" : ChoiceError;
    public bool HasChoicesStatus => ChoicesStatus.Length > 0;
    partial void OnIsLoadingChoicesChanged(bool value) => NotifyChoicesStatus();
    partial void OnChoiceErrorChanged(string value) => NotifyChoicesStatus();
    private void NotifyChoicesStatus()
    {
        OnPropertyChanged(nameof(CanSelectChoice));
        OnPropertyChanged(nameof(ChoicesStatus));
        OnPropertyChanged(nameof(HasChoicesStatus));
    }
    partial void OnSelectedChoiceChanged(AutomationChoice? value)
    {
        if (!_updatingChoices && value != null) { Value = value.Value; ChoiceError = ""; }
    }
    internal void BeginChoicesLoad() { IsLoadingChoices = true; ChoiceError = ""; }
    internal void FailChoicesLoad(string error)
    {
        if (IsChoice) ApplyChoices(Choices);
        ChoiceError = error;
        IsLoadingChoices = false;
    }
    internal void ApplyChoices(IReadOnlyList<AutomationChoice> choices)
    {
        if (IsMultiChoice)
        {
            Choices = choices;
            RebuildMultiChoices();
            if (choices.Count == 0) ChoiceError = "没有可用选项，请检查配置后重新读取";
            IsLoadingChoices = false;
            return;
        }
        _updatingChoices = true;
        try
        {
            Choices = !string.IsNullOrWhiteSpace(Value) && !choices.Any(choice => choice.Value == Value)
                ? new[] { new AutomationChoice(Value, Value + "（当前值）") }.Concat(choices).ToArray() : choices;
            SelectedChoice = Choices.FirstOrDefault(choice => choice.Value == Value);
        }
        finally { _updatingChoices = false; }
        ChoiceError = choices.Count == 0 ? "没有可用选项，请检查配置后重新读取"
            : !string.IsNullOrWhiteSpace(Value) && !choices.Any(choice => choice.Value == Value) ? "当前值未列在候选项中，运行时由流程校验" : "";
        IsLoadingChoices = false;
    }

    [ObservableProperty] private IReadOnlyList<AutomationMultiChoiceViewModel> _multiChoices = [];
    public string SelectionSummary => MultiChoices.Any(choice => choice.IsSelected)
        ? string.Join("、", MultiChoices.Where(choice => choice.IsSelected).Select(choice => choice.Label)) : "未选择";

    private void RebuildMultiChoices()
    {
        string[] selected;
        try
        {
            selected = JsonSerializer.Deserialize<string[]>(Value) ?? throw new JsonException();
            ChoiceError = selected.Any(value => value is null || !Choices.Any(choice => choice.Value == value))
                ? "保存的选项已不可用，请重新勾选或清空。" : "";
        }
        catch (JsonException)
        {
            selected = [];
            ChoiceError = "参数格式已更新，请重新勾选或清空。";
        }
        MultiChoices = Choices.Select(choice => new AutomationMultiChoiceViewModel(choice,
            selected.Contains(choice.Value, StringComparer.Ordinal), SaveMultiSelection)).ToArray();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void SaveMultiSelection()
    {
        if (_updatingChoices || !IsMultiChoice || Field.IsReadOnly) return;
        _updatingChoices = true;
        try { Value = JsonSerializer.Serialize(MultiChoices.Where(choice => choice.IsSelected).Select(choice => choice.Value).ToArray()); }
        finally { _updatingChoices = false; }
        ChoiceError = "";
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand] private void SelectAllChoices() => SetAllChoices(true);
    [RelayCommand] private void ClearChoices() => SetAllChoices(false);
    private void SetAllChoices(bool selected)
    {
        if (!CanSelectChoice) return;
        _updatingChoices = true;
        try { foreach (var choice in MultiChoices) choice.IsSelected = selected; }
        finally { _updatingChoices = false; }
        SaveMultiSelection();
    }

    [ObservableProperty] private string _value;

    internal void RefreshGlobalValue()
    {
        // 全局值只作留空时的回退：不覆盖任务已填的值，也不把它们变成「使用全局值」。
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(Hint));
    }

    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(BoolValue));
        if (!_updatingChoices)
        {
            if (IsMultiChoice) RebuildMultiChoices();
            else SelectedChoice = Choices.FirstOrDefault(choice => choice.Value == value);
        }
        _changed();
    }
}

public sealed partial class AutomationMultiChoiceViewModel(AutomationChoice choice, bool selected, Action changed) : ObservableObject
{
    public string Value => choice.Value;
    public string Label => choice.Label;
    public string ToolTip => choice.ToolTip;
    [ObservableProperty] private bool _isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}

internal static class AutomationPalette
{
    public static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var firstLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine;
    }

    // 暗色下状态文字与节点轮廓分开取色，避免文字过暗或整个节点描边过亮。
    public static IBrush Success => new SolidColorBrush(Color.Parse(IsDark ? "#4ADE80" : "#15803D"));
    public static IBrush Danger => new SolidColorBrush(Color.Parse(IsDark ? "#F87171" : "#DC2626"));
    public static IBrush Primary => new SolidColorBrush(Color.Parse(IsDark ? "#82B4F5" : "#1769E0"));
    public static IBrush Secondary => new SolidColorBrush(Color.Parse(IsDark ? "#B8B8B2" : "#64748B"));
    public static IBrush NodeOutline => new SolidColorBrush(Color.Parse(IsDark ? "#5D5D58" : "#94A3B8"));
    public static IBrush SuccessOutline => IsDark ? new SolidColorBrush(Color.Parse("#2E8658")) : Success;
    public static IBrush DangerOutline => IsDark ? new SolidColorBrush(Color.Parse("#B66565")) : Danger;
    public static IBrush PrimaryOutline => IsDark ? new SolidColorBrush(Color.Parse("#507DB0")) : Primary;
    public static IBrush WarningOutline => IsDark ? new SolidColorBrush(Color.Parse("#A88744")) : Warning;

    // ThemeService 在广播前更新 Current，避免控件主题变化时读到应用尚未更新的旧值。
    private static bool IsDark => ThemeService.Current == "dark";

    // 页面在实际主题变化时刷新绑定，已结束的运行和节点也会重新取色。
    public static IBrush Warning => new SolidColorBrush(Color.Parse(IsDark ? "#FBBF24" : "#B45309"));
    public static IBrush Muted => new SolidColorBrush(Color.Parse(IsDark ? "#92928C" : "#94A3B8"));
    public static IBrush SuccessSoft => new SolidColorBrush(Color.Parse(IsDark ? "#12271E" : "#ECFDF3"));
    public static IBrush DangerSoft => new SolidColorBrush(Color.Parse(IsDark ? "#2C1517" : "#FEF2F2"));
    public static IBrush WarningSoft => new SolidColorBrush(Color.Parse(IsDark ? "#2B2110" : "#FFFBEB"));
    public static IBrush PrimarySoft => new SolidColorBrush(Color.Parse(IsDark ? "#17264A" : "#EFF6FF"));
    public static IBrush MutedSoft => new SolidColorBrush(Color.Parse(IsDark ? "#40403D" : "#F1F5F9"));

    public static IBrush StatusBackground(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Succeeded => SuccessSoft,
        AutomationRunStatus.Failed => DangerSoft,
        AutomationRunStatus.Running or AutomationRunStatus.Queued => PrimarySoft,
        AutomationRunStatus.Cancelled or AutomationRunStatus.AwaitingReview => WarningSoft,
        _ => MutedSoft
    };

    public static IBrush Status(AutomationRunStatus? status, bool problem) => problem ? Warning : status switch
    {
        AutomationRunStatus.Succeeded => Success,
        AutomationRunStatus.Failed => Danger,
        AutomationRunStatus.Running or AutomationRunStatus.Queued => Primary,
        AutomationRunStatus.Cancelled or AutomationRunStatus.AwaitingReview => Warning,
        _ => Secondary
    };

    public static string StatusText(AutomationRunStatus status) => status switch
    {
        AutomationRunStatus.Queued => "排队中",
        AutomationRunStatus.Running => "运行中",
        AutomationRunStatus.Succeeded => "成功",
        AutomationRunStatus.Skipped => "已跳过",
        AutomationRunStatus.Failed => "失败",
        AutomationRunStatus.Cancelled => "已取消",
        AutomationRunStatus.AwaitingReview => "待审核",
        _ => status.ToString()
    };
}

public sealed record AutomationStepChip(int Index, string Title, bool IsLast, string Key = "",
    IReadOnlyList<string>? DependsOn = null, IReadOnlyList<AutomationStep>? Children = null) : Heron.ClientControls.Graphs.IGraphItem
{
    public bool HasNext => !IsLast;
    public bool HasChildren => Children is { Count: > 0 };
}
