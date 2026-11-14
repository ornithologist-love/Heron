using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace Heron.Pregel.Automation;

/// <summary>Reads saved tasks and run snapshots, letting the embedding product migrate its older formats first.</summary>
internal sealed class AutomationTaskJsonConverter : JsonConverter<AutomationTask>
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> PlainOptions = new();
    private static JsonSerializerOptions Plain(JsonSerializerOptions options) => PlainOptions.GetValue(options, source =>
    {
        var plain = new JsonSerializerOptions(source);
        for (var i = plain.Converters.Count - 1; i >= 0; i--)
            if (plain.Converters[i] is AutomationTaskJsonConverter) plain.Converters.RemoveAt(i);
        return plain;
    });

    public override AutomationTask? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var task = JsonNode.Parse(ref reader)?.AsObject() ?? throw new JsonException("任务不能为空");
        AutomationRuntime.TaskMigration?.Invoke(task);
        return task.Deserialize<AutomationTask>(Plain(options));
    }

    public override void Write(Utf8JsonWriter writer, AutomationTask value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Plain(options));
}
