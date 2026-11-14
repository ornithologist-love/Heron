using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    public ObservableCollection<AutomationTaskItemViewModel> VisibleTasks { get; } = [];
    public ObservableCollection<AutomationTemplateOption> VisibleTemplates { get; } = [];
    public IReadOnlyList<AutomationTaskFilterViewModel> TaskFilters { get; } =
        [new("全部"), new("失败"), new("运行中"), new("定时")];

    [ObservableProperty] private string _taskSearch = "";
    [ObservableProperty] private int _taskFilterIndex;
    [ObservableProperty] private string _templateSearch = "";
    [ObservableProperty] private AutomationTemplateOption? _selectedTemplate;

    internal bool IsUpdatingTaskList { get; private set; }
    public bool CanReorderTasks => string.IsNullOrWhiteSpace(TaskSearch) && TaskFilterIndex == 0;
    public bool HasVisibleTasks => VisibleTasks.Count > 0;
    public bool HasVisibleTemplates => VisibleTemplates.Count > 0;
    public string TaskCountText => $"{VisibleTasks.Count(task => !task.IsDraft)} / {Tasks.Count(task => !task.IsDraft)} 项任务";
    public string TaskListEmptyText => Tasks.Count == 0 ? EmptyText : "没有匹配的任务，请调整搜索或筛选。";
    public string TaskOrderTip => CanReorderTasks ? "长按任务后拖动排序；未保存草稿固定置顶" : "清空搜索并选择「全部」后可拖动排序";

    partial void OnTaskSearchChanged(string value) => RefreshTaskList();
    partial void OnTaskFilterIndexChanged(int value) => RefreshTaskList();
    partial void OnTemplateSearchChanged(string value) => RefreshTemplateList();
    partial void OnIsTemplateMenuOpenChanged(bool value)
    {
        if (!value) return;
        TemplateSearch = "";
        RefreshTemplateList();
    }

    private static bool Matches(string query, params string[] values) =>
        values.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private void RefreshTemplateList()
    {
        var query = TemplateSearch.Trim();
        SynchronizeList(VisibleTemplates, Templates.Where(template => Matches(query, template.Title, template.Description)).ToArray());
        if (SelectedTemplate == null || !VisibleTemplates.Contains(SelectedTemplate))
            SelectedTemplate = VisibleTemplates.FirstOrDefault();
        OnPropertyChanged(nameof(HasVisibleTemplates));
    }

    private void RefreshTaskList()
    {
        var saved = Tasks.Where(task => !task.IsDraft).ToArray();
        TaskFilters[0].Count = saved.Length;
        TaskFilters[1].Count = saved.Count(task => task.IsCardFailed);
        TaskFilters[2].Count = saved.Count(task => task.IsCardRunning);
        TaskFilters[3].Count = saved.Count(task => task.ScheduleEnabled);
        if (SelectedTask is { } selected && !Tasks.Contains(selected)) SelectedTask = null;
        var query = TaskSearch.Trim();
        var rows = Tasks.Where(task => task.IsDraft ||
            (TaskFilterIndex switch { 1 => task.IsCardFailed, 2 => task.IsCardRunning,
                3 => task.ScheduleEnabled, _ => true })
            && Matches(query, task.Name, task.Title)).ToArray();
        IsUpdatingTaskList = true;
        try
        {
            SynchronizeList(VisibleTasks, rows);
            // Filtering is navigation only: keep the selected task and its unsaved form.
            OnPropertyChanged(nameof(SelectedTask));
        }
        finally { IsUpdatingTaskList = false; }
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(TaskCountText));
        OnPropertyChanged(nameof(TaskListEmptyText));
        OnPropertyChanged(nameof(CanReorderTasks));
        OnPropertyChanged(nameof(TaskOrderTip));
    }

    private static void SynchronizeList<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        var wanted = rows.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
            if (!wanted.Contains(target[index])) target.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], rows[index])) continue;
            var current = target.IndexOf(rows[index]);
            if (current < 0) target.Insert(index, rows[index]);
            else if (current != index) target.Move(current, index);
        }
    }
}

public sealed partial class AutomationTaskFilterViewModel(string label) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Text))] private int _count;
    public string Text => Count > 0 ? $"{label} {Count}" : label;
}
