using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationEditorViewModel
{
    /// <summary>The task's calendar triggers; they share the 「定时触发」 switch and are saved in this order.</summary>
    public ObservableCollection<AutomationScheduleViewModel> Schedules { get; } = [];
    public bool HasMultipleSchedules => Schedules.Count > 1;
    public string ScheduleSummary => AutomationSchedules.Summary(BuildTriggers());
    public string TriggerSummary => (ManualEnabled, ScheduleEnabled) switch
    {
        (true, true) => $"手动 · {ScheduleSummary}",
        (true, false) => "手动",
        (false, true) => ScheduleSummary,
        _ => "未启用触发器"
    };
    public string ScheduleTriggerDescription => ScheduleEnabled ? ScheduleSummary : "未启用，时间配置保留";
    /// <summary>Shown while editing; saving is blocked by the host rule with the same message.</summary>
    public string ScheduleConflict => AutomationSchedules.Conflict(BuildTriggers()) ?? "";
    public bool HasScheduleConflict => ScheduleConflict.Length > 0;
    [ObservableProperty] private bool _isScheduleDetailsExpanded;

    private bool _initializingSchedule;
    private void InitializeSchedules(IReadOnlyList<AutomationTrigger> triggers)
    {
        _initializingSchedule = true;
        Schedules.Clear();
        foreach (var trigger in triggers) Schedules.Add(new AutomationScheduleViewModel(trigger, ScheduleChanged, RemoveSchedule));
        RenumberSchedules();
        _initializingSchedule = false;
    }

    private IReadOnlyList<AutomationTrigger> BuildTriggers() => Schedules.Select(schedule => schedule.Build()).ToArray();

    [RelayCommand]
    private void AddSchedule()
    {
        // Start one hour after the last trigger, so a fresh row is never an immediate duplicate.
        var last = Schedules[^1].Build();
        var next = last.TryGetTime(out var hour, out var minute) ? last with { DailyTime = $"{(hour + 1) % 24:00}:{minute:00}" } : last;
        Schedules.Add(new AutomationScheduleViewModel(next, ScheduleChanged, RemoveSchedule));
        RenumberSchedules();
        IsScheduleDetailsExpanded = true;
        ScheduleChanged();
    }

    private void RemoveSchedule(AutomationScheduleViewModel schedule)
    {
        if (Schedules.Count <= 1 || !Schedules.Remove(schedule)) return;
        RenumberSchedules();
        ScheduleChanged();
    }

    private void RenumberSchedules()
    {
        for (var i = 0; i < Schedules.Count; i++) Schedules[i].SetPosition(i + 1, Schedules.Count > 1);
        OnPropertyChanged(nameof(HasMultipleSchedules));
    }

    private void ScheduleChanged()
    {
        if (_initializingSchedule) return;
        OnPropertyChanged(nameof(ScheduleSummary)); OnPropertyChanged(nameof(TriggerSummary));
        OnPropertyChanged(nameof(ScheduleTriggerDescription));
        OnPropertyChanged(nameof(ScheduleConflict)); OnPropertyChanged(nameof(HasScheduleConflict));
        MarkDirty();
    }
}

/// <summary>One calendar trigger in the editor.</summary>
public sealed partial class AutomationScheduleViewModel : ObservableObject
{
    public IReadOnlyList<string> Frequencies { get; } = ["每天", "每周", "每月"];
    public IReadOnlyList<int> MonthDays { get; } = Enumerable.Range(1, 31).ToArray();
    public IReadOnlyList<AutomationWeekDay> Days { get; }
    public IReadOnlyList<string> TimeZones { get; }
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(value => value.ToString("00")).ToArray();
    public IReadOnlyList<string> Minutes { get; } = Enumerable.Range(0, 60).Select(value => value.ToString("00")).ToArray();
    [ObservableProperty] private string _time;
    [ObservableProperty] private string _frequency;
    [ObservableProperty] private int _monthDay;
    [ObservableProperty] private bool _lastDayOfMonth;
    [ObservableProperty] private string _timeZone;
    [ObservableProperty] private string _title = "时间设置";
    [ObservableProperty] private bool _canRemove;
    public bool IsWeekly => Frequency == "每周";
    public bool IsMonthly => Frequency == "每月";
    public bool CanSelectMonthDay => !LastDayOfMonth;
    public bool ShowShortMonthHint => IsMonthly && !LastDayOfMonth && MonthDay > 28;
    public string ShortMonthHint => $"没有 {MonthDay} 日的月份将跳过；需要月末执行请选择“最后一天”。";
    public string? SelectedHour
    {
        get => SelectedTime?.Hours.ToString("00");
        set { if (int.TryParse(value, out var hour) && hour is >= 0 and < 24) Time = $"{hour:00}:{SelectedTime?.Minutes ?? 0:00}"; }
    }
    public string? SelectedMinute
    {
        get => SelectedTime?.Minutes.ToString("00");
        set { if (int.TryParse(value, out var minute) && minute is >= 0 and < 60) Time = $"{SelectedTime?.Hours ?? 0:00}:{minute:00}"; }
    }
    public TimeSpan? SelectedTime
    {
        get => new AutomationTrigger(Time).TryGetTime(out var hour, out var minute) ? new TimeSpan(hour, minute, 0) : null;
        set { if (value is { } time) Time = $"{time.Hours:00}:{time.Minutes:00}"; }
    }

    private readonly Action _changed;
    private readonly Action<AutomationScheduleViewModel> _remove;

    public AutomationScheduleViewModel(AutomationTrigger trigger, Action changed, Action<AutomationScheduleViewModel> remove)
    {
        _changed = changed;
        _remove = remove;
        _time = trigger.DailyTime;
        _frequency = trigger.Frequency switch { "weekly" => "每周", "monthly" => "每月", _ => "每天" };
        _monthDay = trigger.MonthDay;
        _lastDayOfMonth = trigger.LastDayOfMonth;
        _timeZone = trigger.TimeZoneId switch { "" => "跟随系统", "China Standard Time" or "Asia/Shanghai" => "北京时间", _ => trigger.TimeZoneId };
        TimeZones = new[] { "跟随系统", "北京时间", _timeZone }.Distinct().ToArray();
        Days = new[] { 1, 2, 3, 4, 5, 6, 0 }.Select(day => new AutomationWeekDay(day,
            (trigger.WeekDays & (1 << day)) != 0, Changed)).ToArray();
    }

    internal void SetPosition(int position, bool several)
    {
        Title = several ? $"时间设置 {position}" : "时间设置";
        CanRemove = several;
    }

    public AutomationTrigger Build() => new(Normalize(Time))
    {
        Frequency = Frequency switch { "每周" => "weekly", "每月" => "monthly", _ => "daily" },
        WeekDays = Days.Where(day => day.Selected).Aggregate(0, (mask, day) => mask | (1 << day.Day)),
        MonthDay = MonthDay, LastDayOfMonth = LastDayOfMonth,
        TimeZoneId = TimeZone switch { "跟随系统" => "", "北京时间" => "China Standard Time", _ => TimeZone }
    };

    private void Changed()
    {
        OnPropertyChanged(nameof(IsWeekly)); OnPropertyChanged(nameof(IsMonthly));
        OnPropertyChanged(nameof(CanSelectMonthDay)); OnPropertyChanged(nameof(ShowShortMonthHint));
        OnPropertyChanged(nameof(ShortMonthHint)); OnPropertyChanged(nameof(SelectedTime));
        OnPropertyChanged(nameof(SelectedHour)); OnPropertyChanged(nameof(SelectedMinute));
        _changed();
    }
    partial void OnTimeChanged(string value) => Changed();
    partial void OnFrequencyChanged(string value) => Changed();
    partial void OnMonthDayChanged(int value) => Changed();
    partial void OnLastDayOfMonthChanged(bool value) => Changed();
    partial void OnTimeZoneChanged(string value) => Changed();
    [RelayCommand] private void SelectWeekdays() { foreach (var day in Days) day.Selected = day.Day is >= 1 and <= 5; }
    [RelayCommand] private void Remove() => _remove(this);

    private static string Normalize(string time)
    {
        var parts = (time ?? "").Trim().Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            ? $"{h:00}:{m:00}" : time ?? "";
    }
}

public sealed partial class AutomationWeekDay : ObservableObject
{
    public int Day { get; }
    public string Label => "周" + "日一二三四五六"[Day];
    private readonly Action _changed;
    [ObservableProperty] private bool _selected;
    public AutomationWeekDay(int day, bool selected, Action changed) { Day = day; _selected = selected; _changed = changed; }
    partial void OnSelectedChanged(bool value) => _changed();
}
