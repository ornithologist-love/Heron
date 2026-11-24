using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Heron.Pregel.Avalonia.ViewModels;

/// <summary>
/// 「运行所选步骤」：在任务编排图上勾选步骤或整段 Stage，只运行这些，其余记为本次未选择运行的跳过。
/// Admission and the step-to-skip mapping live in the automation host; this only collects the choice.
/// </summary>
public sealed partial class AutomationPageViewModel
{
    private readonly HashSet<string> _stepSelection = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectSteps))]
    private bool _isSelectingSteps;

    public int SelectedStepCount => _stepSelection.Count;
    public bool HasStepSelection => _stepSelection.Count > 0;
    public string RunSelectedStepsText => $"运行所选（{_stepSelection.Count}）";

    /// <summary>Only graph templates can skip steps; the task must be runnable right now.</summary>
    public bool ShowSelectSteps => !IsSelectingSteps && ShowPlan && CanRunSelected
        && SavedTask != null && Editor is { } editor && _host.SupportsReexecution(editor.TemplateId);

    // ShowSelectSteps follows run availability and the selected task; recompute whenever those are announced.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        NotifyDeferredContent(e.PropertyName);
        if (e.PropertyName is nameof(CanRunSelected) or nameof(ShowManualRun) or nameof(Editor) or nameof(SelectedTask))
        {
            OnPropertyChanged(nameof(ShowSelectSteps));
            if (e.PropertyName is nameof(SelectedTask) or nameof(Editor)) IsSelectingSteps = false;
        }
    }

    partial void OnShowPlanChanged(bool value)
    {
        if (!value) IsSelectingSteps = false;
        OnPropertyChanged(nameof(ShowSelectSteps));
    }

    partial void OnIsSelectingStepsChanged(bool value) => ClearStepSelection();

    [RelayCommand]
    private void StartStepSelection()
    {
        if (!ShowSelectSteps) return;
        IsSelectingSteps = true;
    }

    [RelayCommand]
    private void CancelStepSelection() => IsSelectingSteps = false;

    [RelayCommand]
    private void ToggleRunStep(AutomationStepItemViewModel? step)
    {
        if (!IsSelectingSteps || step is not { CanSelectForRun: true }) return;
        if (step.IsStage && step.Children.Count > 0)
        {
            var selectAll = !_stepSelection.Contains(step.Key);
            SetSelected(step, selectAll);
            foreach (var child in step.Children.Where(c => c.CanSelectForRun))
                SetSelected(child, selectAll);
        }
        else
        {
            var select = !_stepSelection.Contains(step.Key);
            SetSelected(step, select);
            if (step.ParentStage is { } parent)
            {
                var selectable = parent.Children.Where(c => c.CanSelectForRun).ToArray();
                var allSelected = selectable.Length > 0 && selectable.All(c => _stepSelection.Contains(c.Key));
                SetSelected(parent, allSelected);
            }
        }
        NotifyStepSelection();
    }

    private void SetSelected(AutomationStepItemViewModel item, bool selected)
    {
        if (selected) _stepSelection.Add(item.Key);
        else _stepSelection.Remove(item.Key);
        item.IsRunSelected = selected;
    }

    [RelayCommand]
    private void RunSelectedSteps()
    {
        if (!IsSelectingSteps || SavedTask is not { } task || _stepSelection.Count == 0) return;
        try
        {
            _host.StartSelectedRun(task.Id, new HashSet<string>(_stepSelection, StringComparer.Ordinal));
        }
        catch (InvalidOperationException error)
        {
            task.Flash(error.Message);
            return;
        }
        IsSelectingSteps = false;
        _selectNewestRun = true;
        _historyKey = null;
        Refresh();
        ViewHistory();
    }

    private void ClearStepSelection()
    {
        _stepSelection.Clear();
        // The plan preview is rebuilt on each read; a fresh copy drops every tick mark.
        Editor?.RefreshPlanPreview();
        NotifyStepSelection();
    }

    private void NotifyStepSelection()
    {
        OnPropertyChanged(nameof(SelectedStepCount));
        OnPropertyChanged(nameof(HasStepSelection));
        OnPropertyChanged(nameof(RunSelectedStepsText));
    }
}
