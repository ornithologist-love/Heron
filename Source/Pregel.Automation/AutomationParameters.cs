using System.Text.Json;

namespace Heron.Pregel.Automation;

/// <summary>The same effective input is used for local checks, remote preview and execution.</summary>
public static class AutomationParameters
{
    public static AutomationTask Resolve(IAutomationTemplate template, AutomationTask task)
    {
        if (task.ParametersResolved) return task;
        var values = task.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var field in template.Fields)
        {
            if (field.IsReadOnly) { values.Remove(field.Key); continue; }
            if (values.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value)) continue;
            var global = AutomationInputSchema.GlobalValue(field.Key) ?? field.GlobalValue;
            values[field.Key] = !string.IsNullOrWhiteSpace(global) ? global : field.EmptyValue ?? field.DefaultValue;
        }
        return task with { Parameters = values, ParametersResolved = true };
    }

    public static string? Validate(IAutomationTemplate template, AutomationTask task)
    {
        var effective = Resolve(template, task);
        foreach (var field in template.Fields.Where(field => !field.IsReadOnly && (field.If?.Invoke(effective) ?? true)))
        {
            var value = effective.Parameter(field.Key);
            var choices = field.With ?? field.Choices?.Select(item => new AutomationChoice(item, item)).ToArray();
            if (field.Kind == Heron.Pregel.DisplayFieldKind.MultiChoice)
            {
                string[] selected;
                try { selected = JsonSerializer.Deserialize<string[]>(value) ?? throw new JsonException(); }
                catch (JsonException) { return $"「{field.Label}」的多选参数格式无效。"; }
                if (selected.Any(item => item == null || choices != null && !choices.Any(choice => choice.Value == item)))
                    return $"「{field.Label}」包含无效选项。";
            }
            else if (choices != null && value.Length > 0 && !choices.Any(choice => choice.Value == value))
                return $"「{field.Label}」的选项无效。";
        }
        // Dynamic choices assist selection. Failure to list them is not evidence
        // that a supplied input is invalid; the template owns business validation.
        return template.Validate(effective);
    }
}

public sealed record AutomationLaunchField(string Key, string Label, string? Group);

public sealed record AutomationLaunchSnapshot
{
    public string? TaskId { get; init; }
    public string? TaskName { get; init; }
    public string LocalNickname { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string ScriptRevision { get; init; } = "";
    public string? WorkflowContentRevision { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<AutomationLaunchField> Fields { get; init; } = [];
    public string? RenderedParameters { get; init; }

    public static AutomationLaunchSnapshot Capture(IAutomationTemplate template, AutomationTask task, string revision,
        AutomationPublishedTask? published)
    {
        string? rendered;
        try { rendered = template.RenderParameters(task); }
        catch (Exception) { rendered = null; }
        return new()
        {
            TaskId = published?.Id, TaskName = published?.Name, LocalNickname = task.DisplayName,
            MachineName = Environment.MachineName, ScriptRevision = revision,
            WorkflowContentRevision = (template as AutomationScriptTemplate)?.WorkflowContentRevision,
            Parameters = task.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value),
            Fields = template.Fields.Where(field => !field.IsReadOnly)
                .Select(field => new AutomationLaunchField(field.Key, field.Label, field.Group)).ToArray(),
            RenderedParameters = rendered
        };
    }
}
