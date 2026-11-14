using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using FluentIcons.Common;

namespace Heron.ClientControls.Gallery.Pages;

/// <summary>自动化布局与共享样式的 Mock，数据和交互仅留在内存，不接入宿主或执行引擎。</summary>
public partial class AutomationMockPage : UserControl
{
    private bool _initialized;
    public AutomationMockPage()
    {
        InitializeComponent();
        DataContext = new AutomationLayoutMock();
        _initialized = true;
        ResetParameterNavigation();
    }

    private void ResetParameterNavigation()
    {
        if (!_initialized) return;
        ParameterNavigation.SelectedIndex = 0;
        ParameterScroll.Offset = default;
    }

    private void OnToggleDrawer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AutomationLayoutMock mock) mock.IsDrawerOpen = !mock.IsDrawerOpen;
    }

    private void OnMockAction(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AutomationLayoutMock mock) return;
        var message = (sender as Control)?.Tag?.ToString();
        if (!string.IsNullOrWhiteSpace(message)) mock.SetFeedback(message);
    }

    private void OnShowFullLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AutomationLayoutMock mock) return;
        mock.IsDrawerOpen = true;
        mock.SetFeedback("已展开所选运行的 mock 日志；此预览不会读取真实运行记录。");
    }

    private void OnClearFeedback(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AutomationLayoutMock mock) mock.SetFeedback("");
    }

    private void OnParameterSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is not ListBox { SelectedItem: ListBoxItem { Tag: string name } }) return;
        var section = this.FindControl<Control>(name);
        if (section == null || this.FindControl<StackPanel>("ParameterSections") is not { } sections
            || this.FindControl<ScrollViewer>("ParameterScroll") is not { } scroll) return;
        if (section.TranslatePoint(default, sections) is not { } point) return;
        scroll.Offset = new Vector(0, Math.Clamp(point.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }
}

public abstract class LayoutObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum LayoutState { Idle, Succeeded, Failed, Running, Cancelled, Pending }

/// <summary>带运行状态的 mock 条目，统一给模板提供状态类名开关。</summary>
public abstract record LayoutStateful(LayoutState State)
{
    public bool IsIdle => State == LayoutState.Idle;
    public bool IsSucceeded => State == LayoutState.Succeeded;
    public bool IsFailed => State == LayoutState.Failed;
    public bool IsRunning => State == LayoutState.Running;
    public bool IsCancelled => State == LayoutState.Cancelled;
    public bool IsPending => State == LayoutState.Pending;
    public bool IsQuiet => State is LayoutState.Idle or LayoutState.Pending or LayoutState.Cancelled;

    public string StateText => State switch
    {
        LayoutState.Idle => "尚未运行",
        LayoutState.Succeeded => "成功",
        LayoutState.Failed => "失败",
        LayoutState.Running => "运行中",
        LayoutState.Cancelled => "已取消",
        _ => "未执行"
    };
}

public sealed record LayoutStep(string Name, string Meta, LayoutState State) : LayoutStateful(State);

public sealed record LayoutStage(string Name, string Duration, LayoutState State, IReadOnlyList<LayoutStep> Steps) : LayoutStateful(State);

public sealed record LayoutRun(string Time, string Trigger, LayoutState State, string Duration, string Message) : LayoutStateful(State)
{
    public string Summary => $"{Trigger}触发 · 耗时 {Duration}";
    public string ToolTipText => $"{StateText} · {Summary}\n{Message}";
}

public sealed record LayoutTask(string Name, Icon? CustomIcon, string Description, LayoutState State, string Time) : LayoutStateful(State)
{
    public Icon Icon => CustomIcon ?? FluentIcons.Common.Icon.CalendarClock;
    public string? Schedule { get; init; }
    public string? Problem { get; init; }
    public double Progress { get; init; }
    public bool Paused { get; init; }
    public bool IsDraft { get; init; }
    public bool RequiresReload { get; init; }
    public int Revision { get; init; } = 1;
    public int RemoteRevision { get; init; } = 1;
    public bool HasRevisionMismatch => Revision != RemoteRevision;
    public bool NeedsReload => RequiresReload || HasRevisionMismatch;
    public IReadOnlyList<LayoutRun> Runs { get; init; } = [];

    public bool HasSchedule => Schedule != null;
    public bool HasProblem => Problem != null;
    public bool HasStatus => Problem == null;
    public string TriggerText => Schedule is { } s ? (Paused ? $"{s} · 已暂停，仅手动" : $"{s} · 手动") : "手动";
    public string RowStatus => State == LayoutState.Running ? $"运行中 {Progress:F0}%" : StateText;
}

public sealed record LayoutFilter(string Key, string Label, int Count)
{
    public string Text => Count > 0 ? $"{Label} {Count}" : Label;
}

public sealed class AutomationLayoutMock : LayoutObservable
{
    private readonly IReadOnlyList<LayoutTask> _all;
    private LayoutTask? _selectedTask;
    private LayoutRun? _selectedRun;
    private LayoutFilter? _selectedFilter;
    private string _search = "";
    private bool _isCompact;
    private bool _isDrawerOpen;
    private int _tabIndex;
    private int _widthIndex;
    private string _feedback = "";

    public AutomationLayoutMock()
    {
        _all = BuildTasks();
        Filters =
        [
            new("all", "全部", _all.Count),
            new("failed", "失败", _all.Count(t => t.IsFailed)),
            new("running", "运行中", _all.Count(t => t.IsRunning)),
            new("scheduled", "定时", _all.Count(t => t.HasSchedule))
        ];
        _selectedFilter = Filters[0];
        ApplyFilter();
        SelectedTask = Tasks.FirstOrDefault();
    }

    public ObservableCollection<LayoutTask> Tasks { get; } = [];
    public IReadOnlyList<LayoutFilter> Filters { get; }
    public IReadOnlyList<string> PageTabs { get; } = ["参数设置", "编排", "运行记录"];
    public IReadOnlyList<string> WidthOptions { get; } = ["1100 · 小窗", "1314 · 中窗", "1600 · 宽屏"];
    private static readonly double[] Widths = [1100, 1314, 1600];

    public LayoutFilter? SelectedFilter
    {
        get => _selectedFilter;
        set { if (Set(ref _selectedFilter, value)) ApplyFilter(); }
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) ApplyFilter(); }
    }

    public string TaskCountText => $"{Tasks.Count} / {_all.Count} 项";
    public bool HasTasks => Tasks.Count > 0;
    public string EmptySelectionHint => HasTasks ? "选择任务查看运行记录和设置" : "没有符合条件的任务，请调整搜索词或筛选条件。";
    public string Feedback
    {
        get => _feedback;
        private set { if (Set(ref _feedback, value)) Raise(nameof(HasFeedback)); }
    }
    public bool HasFeedback => Feedback.Length > 0;

    public void SetFeedback(string message) => Feedback = message;

    public LayoutTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!Set(ref _selectedTask, value)) return;
            Raise(nameof(HasTask), nameof(Runs), nameof(HasRuns));
            SelectedRun = value?.Runs.FirstOrDefault();
            TabIndex = value is { IsDraft: true } ? 0
                : value is { NeedsReload: true } ? 1
                : value is { Runs.Count: > 0 } ? 2 : 0;
            Raise(nameof(PlanGraph), nameof(NeedsScriptReload), nameof(ScriptMessage));
        }
    }

    public bool HasTask => SelectedTask != null;
    public IReadOnlyList<LayoutRun> Runs => SelectedTask?.Runs ?? [];
    public bool HasRuns => Runs.Count > 0;

    public LayoutRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (!Set(ref _selectedRun, value)) return;
            Raise(nameof(HasRun), nameof(RunGraph), nameof(LogText), nameof(LogLineCount));
        }
    }

    public bool HasRun => SelectedRun != null;
    public IReadOnlyList<MockGraphNode> RunGraph => SelectedRun is { } run ? BuildGraph(BuildStages(run), plan: false) : [];
    public IReadOnlyList<MockGraphNode> PlanGraph => BuildGraph(BuildStages(
        SelectedTask?.Runs.FirstOrDefault() ?? new LayoutRun("", "", LayoutState.Succeeded, "", "")), plan: true);
    public bool NeedsScriptReload => SelectedTask?.NeedsReload == true;
    public string ScriptMessage => SelectedTask switch
    {
        { HasRevisionMismatch: true } task => $"当前 revision {task.Revision}，远端 revision {task.RemoteRevision}；请重新加载脚本后核对编排。",
        { RequiresReload: true } => "任务模板尚未加载，请重新加载脚本后核对编排。",
        _ => ""
    };

    public string LogText => SelectedRun is { } run ? BuildLog(run) : "";
    public string LogLineCount => $"{LogText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} 行";

    public bool IsCompact
    {
        get => _isCompact;
        set => Set(ref _isCompact, value);
    }

    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set => Set(ref _isDrawerOpen, value);
    }

    public int TabIndex
    {
        get => _tabIndex;
        set { if (Set(ref _tabIndex, value)) Raise(nameof(IsRunsTab), nameof(IsOrchestrationTab), nameof(IsSettingsTab), nameof(CurrentTabName)); }
    }

    public string CurrentTabName => PageTabs[Math.Clamp(TabIndex, 0, PageTabs.Count - 1)];

    public bool IsRunsTab => TabIndex == 2;
    public bool IsOrchestrationTab => TabIndex == 1;
    public bool IsSettingsTab => TabIndex == 0;

    public int WidthIndex
    {
        get => _widthIndex;
        set { if (Set(ref _widthIndex, value)) Raise(nameof(PreviewWidth)); }
    }

    public double PreviewWidth => Widths[Math.Clamp(WidthIndex, 0, Widths.Length - 1)];

    private void ApplyFilter()
    {
        var key = SelectedFilter?.Key ?? "all";
        var keep = SelectedTask;
        Tasks.Clear();
        foreach (var task in _all)
        {
            if (Search.Length > 0 && !task.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                && !task.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var match = key switch
            {
                "failed" => task.IsFailed,
                "running" => task.IsRunning,
                "scheduled" => task.HasSchedule,
                _ => true
            };
            if (match) Tasks.Add(task);
        }
        Raise(nameof(TaskCountText), nameof(HasTasks), nameof(EmptySelectionHint));
        if (keep != null && Tasks.Contains(keep))
        {
            SelectedTask = keep;
        }
        else
        {
            SelectedTask = Tasks.FirstOrDefault();
        }
    }

    private static IReadOnlyList<MockGraphNode> BuildGraph(IReadOnlyList<LayoutStage> stages, bool plan)
    {
        static MockStepState State(LayoutState state) => state switch
        {
            LayoutState.Succeeded => MockStepState.Succeeded,
            LayoutState.Failed => MockStepState.Failed,
            LayoutState.Running => MockStepState.Running,
            LayoutState.Cancelled => MockStepState.Cancelled,
            _ => MockStepState.Pending
        };
        return stages.Select((stage, index) => new MockGraphNode($"stage-{index}", stage.Name,
            plan ? MockStepState.Plan : State(stage.State), index == 0 ? [] : [$"stage-{index - 1}"])
        {
            Duration = plan ? "" : stage.Duration,
            HasLog = !plan,
            Children = stage.Steps.Select((step, stepIndex) => new MockGraphNode($"step-{index}-{stepIndex}", step.Name,
                plan ? MockStepState.Plan : State(step.State), stepIndex == 0 ? [] : [$"step-{index}-{stepIndex - 1}"])
            {
                Detail = plan ? "" : step.Meta,
                HasLog = !plan,
                Progress = step.IsRunning ? 42 : -1
            }).ToArray()
        }).ToArray();
    }

    private static IReadOnlyList<LayoutStage> BuildStages(LayoutRun run)
    {
        var done = LayoutState.Succeeded;
        var pending = LayoutState.Pending;
        var state = run.State;
        return
        [
            new("准备输入", "6 秒", done,
                [new("检查示例参数", "<1 秒", done), new("读取示例数据", "5 秒", done)]),
            new("处理数据", state == LayoutState.Running ? "运行中" : "12 秒", state,
                [new("转换格式", "3 秒", done), new("校验结果", state == LayoutState.Failed ? "示例校验失败" : "9 秒", state)]),
            new("输出结果", state == done ? "2 秒" : "", state == done ? done : pending,
                [new("生成摘要", "", state == done ? done : pending), new("保存输出", "", state == done ? done : pending)])
        ];
    }

    private static string BuildLog(LayoutRun run) => $"""
        [模拟 12:00:00] 准备输入：示例参数检查完成
        [模拟 12:00:06] 处理数据：格式转换完成
        [模拟 12:00:18] 校验结果：{run.StateText}
        此处只展示内存中的示例日志，不执行任务或读写文件。
        """;

    private static IReadOnlyList<LayoutTask> BuildTasks()
    {
        static LayoutRun Run(string time, LayoutState state, string trigger = "手动") =>
            new(time, trigger, state, state == LayoutState.Running ? "进行中" : "20 秒", state switch
            {
                LayoutState.Failed => "示例校验未通过，请检查模拟输入。",
                LayoutState.Running => "正在处理示例数据。",
                LayoutState.Cancelled => "本次模拟已取消。",
                _ => "示例数据处理完成。"
            });
        return
        [
            new("示例数据处理", Icon.Code, "准备输入、处理数据并输出摘要", LayoutState.Failed, "12:00")
            {
                Runs = [Run("12:00", LayoutState.Failed), Run("11:30", LayoutState.Succeeded), Run("昨天 12:00", LayoutState.Cancelled)]
            },
            new("定时汇总示例", Icon.CalendarClock, "展示定时触发与正在运行的状态", LayoutState.Running, "11:45")
            {
                Schedule = "每天 12:00", Progress = 42,
                Runs = [Run("11:45", LayoutState.Running, "定时"), Run("昨天 12:00", LayoutState.Succeeded, "定时")]
            },
            new("手动导出示例", Icon.Play, "展示成功的运行记录", LayoutState.Succeeded, "10:30")
            { Runs = [Run("10:30", LayoutState.Succeeded)] },
            new("暂停的计划", Icon.CalendarClock, "展示暂停状态与计划信息", LayoutState.Succeeded, "昨天")
            { Schedule = "工作日 09:30", Paused = true, Runs = [Run("昨天 09:30", LayoutState.Succeeded, "定时")] },
            new("待更新的示例", Icon.ArrowSync, "展示版本变化的提示样式", LayoutState.Idle, "")
            { Revision = 1, RemoteRevision = 2, Problem = "需要重新加载" },
            new("空白任务", Icon.Add, "展示未运行任务的参数设置", LayoutState.Idle, "")
            { IsDraft = true, Problem = "草稿" }
        ];
    }
}
