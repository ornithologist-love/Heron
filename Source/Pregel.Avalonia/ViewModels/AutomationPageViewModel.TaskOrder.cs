using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    private bool _taskOrderInitialized;
    private bool _isTaskReordering;
    private bool _taskRowsMoving;

    public bool CanResetTaskOrder => Tasks.Count(task => !task.IsDraft) > 1;

    private void MergeTaskRows(IReadOnlyList<AutomationTaskView> views)
    {
        foreach (var stale in Tasks.Where(task => !task.IsDraft && views.All(view => view.Task.Id != task.Id)).ToArray())
            Tasks.Remove(stale);

        if (!_taskOrderInitialized)
        {
            var rows = views.ToDictionary(view => view.Task.Id);
            foreach (var id in _host.TaskOrder.Distinct(StringComparer.Ordinal))
                if (rows.TryGetValue(id, out var view))
                {
                    var row = new AutomationTaskItemViewModel(id);
                    row.Apply(view);
                    Tasks.Add(row);
                }
            _taskOrderInitialized = true;
        }

        // The saved list is authoritative. Only insert new entries in default order; never sort it globally.
        foreach (var view in views.Where(view => Tasks.All(task => task.Id != view.Task.Id)))
        {
            var row = new AutomationTaskItemViewModel(view.Task.Id);
            row.Apply(view);
            var index = _draft != null ? 1 : 0;
            while (index < Tasks.Count && StringComparer.CurrentCulture.Compare(row.Title, Tasks[index].Title) >= 0)
                index++;
            Tasks.Insert(index, row);
        }
        SaveTaskOrder();
    }

    private void MoveTaskRow(int from, int to)
    {
        var selected = SelectedTask;
        _taskRowsMoving = true;
        try
        {
            Tasks.Move(from, to);
            RefreshTaskList();
        }
        finally
        {
            SelectedTask = selected;
            _taskRowsMoving = false;
        }
    }

    public void BeginTaskReorder() => _isTaskReordering = true;

    public bool MoveTask(AutomationTaskItemViewModel row, AutomationTaskItemViewModel target)
    {
        if (!_isTaskReordering || row.IsDraft) return false;
        var from = Tasks.IndexOf(row);
        var to = Tasks.IndexOf(target);
        if (from < 0 || to < 0) return false;
        if (_draft != null) to = Math.Max(1, to);
        if (from == to) return false;
        MoveTaskRow(from, to);
        return true;
    }

    public void EndTaskReorder(bool moved)
    {
        if (moved) SaveTaskOrder();
        _isTaskReordering = false;
        Refresh();
    }

    [RelayCommand]
    private void ResetTaskOrder()
    {
        if (!CanResetTaskOrder || _isTaskReordering) return;
        var offset = _draft != null ? 1 : 0;
        foreach (var view in SortByTemplate(_host.Tasks))
            if (Tasks.FirstOrDefault(task => task.Id == view.Task.Id) is { } row)
                MoveTaskRow(Tasks.IndexOf(row), offset++);
        SaveTaskOrder();
        RefreshTaskList();
    }

    private void SaveTaskOrder()
    {
        if (!_host.IsActive || !_taskOrderInitialized) return;
        try { _host.SaveTaskOrder(Tasks.Where(task => !task.IsDraft).Select(task => task.Id)); }
        catch (Exception ex)
        {
            SelectedTask?.Flash("任务列表顺序未保存：" + ex.Message);
            Serilog.Log.Warning(ex, "[Automation] task order save failed");
        }
    }
}
