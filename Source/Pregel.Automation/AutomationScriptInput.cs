using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Separates automation metadata from the graph's business input.</summary>
public static class AutomationScriptInput
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static PregelInputSchema Parse(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is JsonObject value) value.Remove("triggers");
        return PregelInputSchema.Parse(root?.ToJsonString() ?? "null");
    }

    /// <summary><c>schedule</c> is one object, or an array when the task has several calendar triggers.</summary>
    public sealed record Triggers(bool Manual, bool ScheduleEnabled, IReadOnlyList<Schedule> Schedules)
    {
        [JsonIgnore]
        public string Version => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            FromTask(Apply(new AutomationTask())).ToNode().ToJsonString(Json))));

        public AutomationTask Apply(AutomationTask task) => task.WithSchedules(Schedules.Select(s => s.ToTrigger()).ToArray())
            with { ManualEnabled = Manual, ScheduleEnabled = ScheduleEnabled };

        public static Triggers FromTask(AutomationTask task) => new(task.ManualEnabled, task.ScheduleEnabled,
            task.Schedules.Select(trigger => new Schedule
            {
                Enabled = task.ScheduleEnabled, Time = trigger.DailyTime, Frequency = trigger.Frequency,
                Timezone = trigger.TimeZoneId, WeekDays = Enumerable.Range(1, 7)
                    .Where(day => (trigger.WeekDays & (1 << (day % 7))) != 0).ToArray(),
                MonthDay = trigger.MonthDay, LastDayOfMonth = trigger.LastDayOfMonth
            }).ToArray());

        // A single trigger keeps the legacy object form, so existing files only change when a second one is added.
        public JsonObject ToNode() => new()
        {
            ["manual"] = Manual,
            ["schedule"] = Schedules.Count == 1 ? JsonSerializer.SerializeToNode(Schedules[0], Json)
                : new JsonArray(Schedules.Select(s => JsonSerializer.SerializeToNode(s, Json)).ToArray())
        };
    }

    public sealed record Schedule
    {
        public bool Enabled { get; init; }
        public string Time { get; init; } = "10:00";
        public string Frequency { get; init; } = "daily";
        public string Timezone { get; init; } = "";
        // ISO weekdays: Monday=1 through Sunday=7.
        public int[] WeekDays { get; init; } = [1, 2, 3, 4, 5];
        public int MonthDay { get; init; } = 1;
        public bool LastDayOfMonth { get; init; }

        public AutomationTrigger ToTrigger()
        {
            if (WeekDays == null || WeekDays.Any(day => day is < 1 or > 7))
                throw new JsonException("triggers.schedule.weekDays 必须是 1（周一）至 7（周日）的数组");
            return new(Time) { Frequency = Frequency, TimeZoneId = Timezone,
                WeekDays = WeekDays.Aggregate(0, (mask, day) => mask | (1 << (day % 7))),
                MonthDay = MonthDay, LastDayOfMonth = LastDayOfMonth };
        }
    }

    public static bool HasTriggers(JsonObject root) => ReadTriggers(root) != null;

    internal static Triggers? ReadTriggers(JsonObject root)
    {
        if (!root.ContainsKey("triggers")) return null;
        if (root["triggers"] is not JsonObject value)
            throw new JsonException("triggers 必须是包含 manual 和 schedule 的对象");
        if (value["manual"] is not JsonValue manualValue || !manualValue.TryGetValue<bool>(out var manual))
            throw new JsonException("triggers 必须声明 manual 和 schedule");
        foreach (var key in value.Select(pair => pair.Key))
            if (key is not ("manual" or "schedule")) throw new JsonException($"triggers 不支持字段 {key}");
        var entries = value["schedule"] switch
        {
            JsonObject single => [single],
            JsonArray { Count: > 0 } many => many.Select(entry => entry as JsonObject
                ?? throw new JsonException("triggers.schedule 数组的每一项都必须是对象")).ToArray(),
            _ => throw new JsonException("triggers.schedule 必须是对象，或至少包含一项的对象数组")
        };
        // The calendar switch is task-wide; entries that state it must agree, entries that omit it follow.
        var stated = entries.Where(entry => entry.ContainsKey("enabled"))
            .Select(entry => entry["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var on) ? on
                : throw new JsonException("triggers.schedule.enabled 必须是 true 或 false")).Distinct().ToArray();
        if (stated.Length > 1) throw new JsonException("triggers.schedule 各项的 enabled 必须一致");
        var enabled = stated.Length == 1 && stated[0];
        var schedules = entries.Select(entry => entry.Deserialize<Schedule>(Json)
            ?? throw new JsonException("triggers.schedule 不能为空")).ToArray();
        var triggers = schedules.Select(s => s.ToTrigger()).ToArray();
        if (enabled && AutomationSchedules.Validate(triggers) is { } error) throw new JsonException(error);
        if (AutomationSchedules.Conflict(triggers) is { } duplicate) throw new JsonException(duplicate);
        return new Triggers(manual, enabled, schedules);
    }

    internal sealed class FileState(string path)
    {
        private (DateTime Time, long Length)? _stamp;
        public Triggers? Value { get; private set; }
        public string? Error { get; private set; }

        public void Refresh(bool required)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _stamp = null; Value = null;
                    Error = required ? "输入文件已不存在：" + path : null;
                    return;
                }
                var stamp = (info.LastWriteTimeUtc, info.Length);
                if (_stamp == stamp) return;
                var root = JsonNode.Parse(AutomationStore.ReadShared(path));
                Value = root is JsonObject value ? ReadTriggers(value) : null;
                Error = null;
                _stamp = stamp;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Do not silently fall back to a different schedule when an override is unreadable.
                Value = null; Error = "无法读取脚本触发器：" + ex.Message; _stamp = null;
            }
        }

        public void Save(AutomationTask task, Action persistTask)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
            using var mutex = new Mutex(false, AutomationRuntime.InputMutexPrefix + key);
            var acquired = false;
            try
            {
                try { if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) throw new IOException("脚本输入文件忙，请稍后重试"); }
                catch (AbandonedMutexException) { }
                acquired = true;
                var original = AutomationStore.ReadShared(path);
                var root = JsonNode.Parse(original) as JsonObject ?? throw new JsonException("脚本输入必须是 JSON 对象");
                var latest = ReadTriggers(root);
                if (latest == null || task.TriggerSourceVersion != null && latest.Version != task.TriggerSourceVersion)
                    throw new IOException("脚本触发器已变化，请重新打开任务后再保存");
                root["triggers"] = Triggers.FromTask(task).ToNode();
                var updated = root.ToJsonString(Json) + Environment.NewLine;
                AutomationStore.WriteAtomic(path, updated);
                try { persistTask(); }
                catch
                {
                    // Restore only our own write; an external editor may have changed the file meanwhile.
                    if (AutomationStore.ReadShared(path) == updated) AutomationStore.WriteAtomic(path, original);
                    throw;
                }
                _stamp = null;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }
}
