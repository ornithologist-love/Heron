namespace Heron.Pregel.Automation;

public sealed partial record AutomationTrigger
{
    // Missing fields in existing JSON preserve daily scheduling in the system timezone.
    public string Frequency { get; init; } = "daily";
    public int WeekDays { get; init; } = 62; // Sunday=bit 0; Monday-Friday.
    public int MonthDay { get; init; } = 1;
    public bool LastDayOfMonth { get; init; }
    public string TimeZoneId { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeZoneInfo Zone => string.IsNullOrEmpty(TimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    public string? Validate()
    {
        if (!TryGetTime(out _, out _)) return "请选择有效的执行时间";
        if (Frequency is not ("daily" or "weekly" or "monthly")) return "请选择每天、每周或每月";
        if (Frequency == "weekly" && (WeekDays <= 0 || WeekDays > 127)) return "请至少选择一个星期";
        if (Frequency == "monthly" && !LastDayOfMonth && MonthDay is < 1 or > 31) return "请选择 1 至 31 日或最后一天";
        try { _ = Zone; }
        catch (TimeZoneNotFoundException) { return "当前系统不支持所选时区，请重新选择"; }
        catch (InvalidTimeZoneException) { return "所选时区配置无效"; }
        return null;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{Calendar} {DailyTime}";

    internal string Calendar => Frequency switch
    {
        "weekly" => "每周" + string.Join("、", new[] { 1, 2, 3, 4, 5, 6, 0 }
            .Where(day => (WeekDays & (1 << day)) != 0).Select(day => "日一二三四五六"[day])),
        "monthly" => LastDayOfMonth ? "每月最后一天" : $"每月 {MonthDay} 日",
        _ => "每天"
    };

    /// <summary>Same calendar event: a weekly trigger on all seven days is daily, and zone ids are compared after resolving.</summary>
    public bool IsEquivalentTo(AutomationTrigger other)
    {
        if (!TryGetTime(out var hour, out var minute) || !other.TryGetTime(out var otherHour, out var otherMinute)
            || hour != otherHour || minute != otherMinute || ZoneKey() != other.ZoneKey()) return false;
        var (kind, otherKind) = (Kind(), other.Kind());
        return kind == otherKind && kind switch
        {
            "weekly" => WeekDays == other.WeekDays,
            "monthly" => LastDayOfMonth == other.LastDayOfMonth && (LastDayOfMonth || MonthDay == other.MonthDay),
            _ => true
        };
    }

    private string Kind() => Frequency == "weekly" && WeekDays == 127 ? "daily" : Frequency;

    private string ZoneKey()
    {
        string id;
        try { id = Zone.Id; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneId; }
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) ? windows : id;
    }

    public bool SameSchedule(AutomationTrigger other) => DailyTime == other.DailyTime
        && Frequency == other.Frequency && TimeZoneId == other.TimeZoneId
        && (Frequency != "weekly" || WeekDays == other.WeekDays)
        && (Frequency != "monthly" || LastDayOfMonth == other.LastDayOfMonth && (LastDayOfMonth || MonthDay == other.MonthDay));

    public DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        if (Validate() is { } error) throw new ArgumentException(error);
        TryGetTime(out var hour, out var minute);
        var zone = Zone;
        var first = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var index = 0; index < 370; index++)
        {
            var date = first.AddDays(index);
            if (Frequency == "weekly" && (WeekDays & (1 << (int)date.DayOfWeek)) == 0) continue;
            if (Frequency == "monthly" && date.Day != (LastDayOfMonth ? DateTime.DaysInMonth(date.Year, date.Month) : MonthDay)) continue;
            var wall = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(wall)) continue; // DST gap: skip this occurrence.
            // DST fold: choose the first instant, never execute twice for the same calendar day.
            var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
            var candidate = new DateTimeOffset(wall, offset);
            if (candidate > after) return candidate;
        }
        throw new InvalidOperationException("无法计算下一次执行时间");
    }

    public DateTime NextLocalRun(DateTime after) => NextOccurrence(new DateTimeOffset(after)).LocalDateTime;
}

/// <summary>A task's calendar triggers act as one schedule: the earliest due occurrence fires, coinciding ones fire once.</summary>
public static class AutomationSchedules
{
    public static string Summary(IReadOnlyList<AutomationTrigger> schedules) =>
        schedules.Select(s => (s.Calendar, s.TimeZoneId)).Distinct().Count() == 1
            ? $"{schedules[0].Calendar} {string.Join("、", schedules.Select(s => s.DailyTime))}"
            : string.Join("；", schedules.Select(s => s.Summary));

    /// <summary>First invalid trigger, prefixed with its position when there are several.</summary>
    public static string? Validate(IReadOnlyList<AutomationTrigger> schedules)
    {
        for (var i = 0; i < schedules.Count; i++)
            if (schedules[i].Validate() is { } error) return schedules.Count == 1 ? error : $"定时触发 {i + 1}：{error}";
        return null;
    }

    /// <summary>Two triggers describing the same event are a configuration mistake, never a reason to run twice.</summary>
    public static string? Conflict(IReadOnlyList<AutomationTrigger> schedules)
    {
        for (var j = 1; j < schedules.Count; j++)
            for (var i = 0; i < j; i++)
                if (schedules[i].IsEquivalentTo(schedules[j]))
                    return $"定时触发 {j + 1} 与定时触发 {i + 1} 重复（{schedules[j].Summary}），请删除或修改其中一个";
        return null;
    }

    public static bool Same(IReadOnlyList<AutomationTrigger> left, IReadOnlyList<AutomationTrigger> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.SameSchedule(pair.Second));

    public static DateTimeOffset NextOccurrence(IReadOnlyList<AutomationTrigger> schedules, DateTimeOffset after)
    {
        if (Validate(schedules) is { } error) throw new ArgumentException(error);
        return schedules.Min(s => s.NextOccurrence(after));
    }

    public static string? ValidateSchedules(this AutomationTask task) => Validate(task.Schedules) ?? Conflict(task.Schedules);
    public static bool SameSchedules(this AutomationTask task, AutomationTask other) => Same(task.Schedules, other.Schedules);
    public static DateTimeOffset NextScheduleOccurrence(this AutomationTask task, DateTimeOffset after) => NextOccurrence(task.Schedules, after);
    public static DateTime NextScheduleLocalRun(this AutomationTask task, DateTime after) =>
        NextOccurrence(task.Schedules, new DateTimeOffset(after)).LocalDateTime;
}
