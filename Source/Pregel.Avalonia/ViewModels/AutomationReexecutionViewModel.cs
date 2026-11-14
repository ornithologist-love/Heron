using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heron.Pregel;

namespace Heron.Pregel.Avalonia.ViewModels;

/// <summary>A run derived from the selected one, offered as a link in the run summary.</summary>
public sealed record AutomationDerivedRunLink(string RunId, string Text);

/// <summary>
/// 失败节点上的「重试」（从失败处继续）与「从此节点重新运行」：入口资格、先预检后确认、来源与派生链接。
/// Admission rules live in <see cref="AutomationHost"/>; this view model only asks and presents.
/// </summary>
public sealed partial class AutomationReexecutionViewModel : ObservableObject
{
    private const string ContinueTip = "从失败处继续：用原参数新建一条记录，已完成的步骤复用原结果";
    private readonly AutomationHost _host;
    private readonly Action<string> _selectRun;
    private readonly Action<string> _started;
    private string? _taskId;
    private AutomationRunItemViewModel? _run;
    private PregelReexecutionRequest? _pending;
    private CancellationTokenSource? _check;

    internal AutomationReexecutionViewModel(AutomationHost host, Action<string> selectRun, Action<string> started)
    {
        _host = host;
        _selectRun = selectRun;
        _started = started;
    }

    // ===== 入口 =====
    [ObservableProperty] private bool _showContinue;
    [ObservableProperty] private bool _canContinue;
    [ObservableProperty] private string _continueToolTip = ContinueTip;

    // ===== 确认面板 =====
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmSummary = "";
    [ObservableProperty] private string _confirmProblem = "";
    [ObservableProperty] private bool _isStarting;
    public bool HasConfirmProblem => ConfirmProblem.Length > 0;
    public bool HasConfirmSummary => ConfirmSummary.Length > 0 && !HasConfirmProblem;
    public bool CanConfirm => IsConfirmOpen && !IsChecking && !IsStarting && !HasConfirmProblem && _pending != null;
    public string DismissText => HasConfirmProblem ? "关闭" : "取消";

    // ===== 来源与派生 =====
    [ObservableProperty] private string _sourceCaption = "";
    [ObservableProperty] private string _sourceLinkText = "";
    [ObservableProperty] private bool _canOpenSource;
    public bool HasSource => SourceCaption.Length > 0;
    public ObservableCollection<AutomationDerivedRunLink> DerivedRuns { get; } = [];
    public bool HasDerivedRuns => DerivedRuns.Count > 0;

    /// <summary>Re-evaluates entries for the selected run. Called on selection and history changes.</summary>
    internal void Update(string? taskId, AutomationRunItemViewModel? run, IReadOnlyList<AutomationRunItemViewModel> runs)
    {
        if (!ReferenceEquals(run, _run) || taskId != _taskId) CloseConfirm();
        _taskId = taskId;
        _run = run;
        var record = run?.Record;
        var supported = taskId != null && record is { IsFinished: true, TaskSnapshot: { } snapshot }
            && record.Status != AutomationRunStatus.Skipped && _host.SupportsReexecution(snapshot.TemplateId);

        ShowContinue = supported && record!.Status is AutomationRunStatus.Failed or AutomationRunStatus.Cancelled;
        var continueProblem = ShowContinue ? _host.ReexecutionProblem(taskId!, record!.RunId, PregelReexecutionMode.Continue) : null;
        CanContinue = ShowContinue && continueProblem == null;
        ContinueToolTip = continueProblem ?? ContinueTip;

        var rerunAllowed = supported && _host.ReexecutionProblem(taskId!, record!.RunId, PregelReexecutionMode.FromNode) == null;
        if (run != null)
        {
            MarkRerunnable(run.Steps, rerunAllowed);
            MarkRetry(run.Steps, ShowContinue);
        }

        UpdateLineage(record, runs);
    }

    [RelayCommand]
    private Task RequestContinueAsync() => OpenConfirmAsync(new(PregelReexecutionMode.Continue, []), "从失败处重试？");

    [RelayCommand]
    private Task RequestRerunAsync(AutomationStepItemViewModel? step)
    {
        if (step is not { CanRerun: true } || _run == null) return Task.CompletedTask;
        var path = NodePath(_run.Steps, step, []);
        return path == null ? Task.CompletedTask
            : OpenConfirmAsync(new(PregelReexecutionMode.FromNode, path), $"从「{step.Title}」重新运行？");
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm || _taskId == null || _run == null || _pending == null) return;
        IsStarting = true;
        try
        {
            var id = _host.StartReexecution(_taskId, _run.RunId, _pending);
            CloseConfirm();
            _started(id);
        }
        catch (InvalidOperationException error) { ConfirmProblem = error.Message; }
        finally { IsStarting = false; }
    }

    [RelayCommand]
    private void CancelConfirm() => CloseConfirm();

    [RelayCommand]
    private void OpenSource()
    {
        if (_run?.Record.SourceRunId is { } source && CanOpenSource) _selectRun(source);
    }

    [RelayCommand]
    private void OpenDerived(AutomationDerivedRunLink? link)
    {
        if (link != null) _selectRun(link.RunId);
    }

    private async Task OpenConfirmAsync(PregelReexecutionRequest request, string title)
    {
        if (_taskId is not { } taskId || _run is not { } run) return;
        _check?.Cancel();
        var check = _check = new CancellationTokenSource();
        _pending = request;
        ConfirmTitle = title;
        ConfirmSummary = "";
        ConfirmProblem = "";
        IsConfirmOpen = true;
        IsChecking = true;
        try
        {
            var result = await _host.CheckReexecutionAsync(taskId, run.RunId, request, check.Token);
            if (check.IsCancellationRequested) return;
            if (result.Problem is { } problem) ConfirmProblem = problem;
            else ConfirmSummary = Summary(run, request, result);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(check, _check)) IsChecking = false;
        }
    }

    private void CloseConfirm()
    {
        _check?.Cancel();
        _check = null;
        _pending = null;
        IsConfirmOpen = false;
        IsChecking = false;
        ConfirmProblem = "";
        ConfirmSummary = "";
    }

    private static string Summary(AutomationRunItemViewModel run, PregelReexecutionRequest request, AutomationReexecutionCheck check)
    {
        var reused = Visible(run.Record.Steps).Count(step => step.NodeIds.Count > 0 && step.NodeIds.All(check.ReusedNodeIds.Contains));
        var scope = request.Mode == PregelReexecutionMode.Continue
            ? "从失败或取消处继续执行"
            : $"「{StepTitle(run.Record, request.NodePath[^1].Value) ?? request.NodePath[^1].Value}」及之后的步骤重新执行，同一批的其他步骤也可能重新执行";
        return (reused > 0 ? $"复用 {reused} 个已完成步骤的结果；" : "") + scope + "。";

        static IEnumerable<AutomationStepRecord> Visible(IEnumerable<AutomationStepRecord> steps) =>
            steps.SelectMany(step => step.IsStage ? Visible(step.Children) : [step]);
    }

    /// <summary>Only a scheduled node of this run's own history can be a restore point; reused nodes belong to the source.</summary>
    private static void MarkRerunnable(IEnumerable<AutomationStepItemViewModel> steps, bool allowed)
    {
        foreach (var step in steps)
        {
            step.CanRerun = allowed && step is { IsPlan: false, IsStage: false, IsRecovery: false, IsFinally: false, IsReused: false }
                && step.NodeIds.Count == 1
                && step.State is not (AutomationStepState.Pending or AutomationStepState.Queued or AutomationStepState.NotRun);
            MarkRerunnable(step.Children, allowed);
        }
    }

    /// <summary>
    /// Retry sits on the node that stopped the run, not in the run header. It continues from the failure,
    /// so it replaces that node's 「从此节点重新运行」 to avoid two look-alike buttons.
    /// </summary>
    private static void MarkRetry(IEnumerable<AutomationStepItemViewModel> steps, bool allowed)
    {
        foreach (var step in steps)
        {
            step.CanRetry = allowed && step is { IsPlan: false, IsStage: false, IsReused: false }
                && step.State is AutomationStepState.Failed or AutomationStepState.Cancelled;
            if (step.CanRetry) step.CanRerun = false;
            MarkRetry(step.Children, allowed);
        }
    }

    private static IReadOnlyList<NodeId>? NodePath(IEnumerable<AutomationStepItemViewModel> items,
        AutomationStepItemViewModel target, IReadOnlyList<NodeId> parents)
    {
        foreach (var item in items)
        {
            // A stage groups nodes of the same graph; only a real subgraph invocation adds a path segment.
            var address = item.IsStage ? parents : parents.Concat(item.NodeIds.Select(id => new NodeId(id))).ToArray();
            if (ReferenceEquals(item, target)) return address;
            if (NodePath(item.Children, target, address) is { } found) return found;
        }
        return null;
    }

    private void UpdateLineage(AutomationRunRecord? record, IReadOnlyList<AutomationRunItemViewModel> runs)
    {
        var source = record?.SourceRunId is { } sourceId ? runs.FirstOrDefault(r => r.RunId == sourceId) : null;
        SourceCaption = record?.SourceRunId == null ? ""
            : record.ReexecutionMode == nameof(PregelReexecutionMode.Continue) ? "从失败处重试"
            : record.ReexecutionNodePath.Count > 0
                ? $"从「{StepTitle(record, record.ReexecutionNodePath[^1]) ?? record.ReexecutionNodePath[^1]}」重新运行"
                : "从节点重新运行";
        CanOpenSource = source != null;
        SourceLinkText = source != null ? source.Title + " 的运行" : "原记录已不在近期历史中";

        DerivedRuns.Clear();
        if (record != null)
            foreach (var derived in runs.Where(r => r.Record.SourceRunId == record.RunId))
                DerivedRuns.Add(new(derived.RunId, $"{derived.Title} · {derived.TriggerText} · {derived.StatusText}"));
        OnPropertyChanged(nameof(HasDerivedRuns));
    }

    internal static string? StepTitle(AutomationRunRecord record, string nodeId)
    {
        return Find(record.Steps);

        string? Find(IEnumerable<AutomationStepRecord> steps)
        {
            foreach (var step in steps)
            {
                if (!step.IsStage && step.NodeIds.Contains(nodeId)) return step.Title;
                if (Find(step.Children) is { } found) return found;
            }
            return null;
        }
    }

    partial void OnConfirmProblemChanged(string value) => NotifyConfirm();
    partial void OnConfirmSummaryChanged(string value) => NotifyConfirm();
    partial void OnIsCheckingChanged(bool value) => NotifyConfirm();
    partial void OnIsStartingChanged(bool value) => NotifyConfirm();
    partial void OnIsConfirmOpenChanged(bool value) => NotifyConfirm();
    partial void OnSourceCaptionChanged(string value) => OnPropertyChanged(nameof(HasSource));

    private void NotifyConfirm()
    {
        OnPropertyChanged(nameof(HasConfirmProblem));
        OnPropertyChanged(nameof(HasConfirmSummary));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(DismissText));
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}
