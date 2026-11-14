using System.ComponentModel;
using System.Reflection;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Reads class-level UI display metadata for compiled and CSX automation templates.</summary>
internal static class AutomationTemplateMetadata
{
    public static string Title(Type type) =>
        type.GetCustomAttribute<DisplayNameAttribute>(inherit: true)?.DisplayName ?? type.Name;

    public static string Description(Type type) =>
        type.GetCustomAttribute<DisplayDescriptionAttribute>(inherit: true)?.Description ?? "";

    public static string Icon(Type type) =>
        type.GetCustomAttribute<DisplayIconAttribute>(inherit: true)?.Name ?? "CalendarClock";
}
