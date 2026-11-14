using Heron.Pregel.Automation;
using System.Linq;

namespace Heron.AutomationTenants;

/// <summary>
/// A task's saved triggers travel with the task list, so the server can adopt them as the team schedule. The server
/// then dispatches those occurrences itself and tells the machine which ones it owns.
/// </summary>
public sealed partial class RemoteAutomationAgent
{
    // Team schedules are always evaluated in Beijing time (UTC+8).
    private const string TeamTimeZone = "China Standard Time";

    internal static RemoteAutomationTriggers TriggersOf(AutomationTask task) => new()
    {
        Manual = task.ManualEnabled,
        ScheduleEnabled = task.ScheduleEnabled,
        Schedules = task.Schedules.Select(schedule => new RemoteAutomationSchedule
        {
            Time = schedule.DailyTime, Frequency = schedule.Frequency, WeekDays = schedule.WeekDays,
            MonthDay = schedule.MonthDay, LastDayOfMonth = schedule.LastDayOfMonth, TimeZone = ZoneOf(schedule)
        }).ToArray(),
        ReadOnlyReason = task.TriggerConfigurationError ?? ""
    };

    // The zone the trigger really runs in, so "follow this machine" is not mistaken for Beijing time elsewhere.
    private static string ZoneOf(AutomationTrigger trigger)
    {
        try
        {
            var id = trigger.Zone.Id;
            return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windows) ? windows : id;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return trigger.TimeZoneId; }
    }

    private void ApplyServerSchedules(RemoteAutomationServerSchedule[] schedules) =>
        automation.UpdateServerSchedules(schedules.GroupBy(schedule => schedule.TaskId).ToDictionary(group => group.Key,
            group => (IReadOnlyList<AutomationTrigger>)group.SelectMany(schedule => schedule.Entries).Select(entry =>
                new AutomationTrigger(entry.Time)
                {
                    Frequency = entry.Frequency, WeekDays = entry.WeekDays, MonthDay = entry.MonthDay,
                    LastDayOfMonth = entry.LastDayOfMonth, TimeZoneId = TeamTimeZone
                }).ToArray()));
}
