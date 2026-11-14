namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    private readonly Dictionary<string, AutomationScriptInput.FileState> _scriptInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AutomationTask> _observedTriggers = [];
    private bool _triggersObserved;
    private readonly Dictionary<string, (AutomationTask Source, string? Path,
        AutomationScriptInput.Triggers? Triggers, string? Error, AutomationTask Resolved)> _resolvedTriggers = new(StringComparer.Ordinal);

    /// <summary>Input.json triggers override the stored task; built-in and legacy tasks retain their settings.</summary>
    public AutomationTask ResolveTaskConfiguration(AutomationTask task)
    {
        lock (_gate) return ResolveTriggersLocked(task);
    }

    private string? ScriptInputPath(AutomationTask task)
    {
        if (!task.TemplateId.StartsWith("script.", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrWhiteSpace(task.ScriptInputPath))
            return (AutomationRuntime.ScriptInputPath?.Invoke(Path.GetFullPath(task.ScriptInputPath)) ?? Path.GetFullPath(task.ScriptInputPath));
        var script = Scripts.LoadedScriptPath(task.TemplateId) ?? Scripts.SavedScriptPath(task.TemplateId);
        return script == null ? null : (AutomationRuntime.ScriptInputPath?.Invoke(Path.Combine(Path.GetDirectoryName(script)!, "Input.json")) ?? Path.Combine(Path.GetDirectoryName(script)!, "Input.json"));
    }

    private AutomationScriptInput.FileState InputState(string path, bool required)
    {
        if (!_scriptInputs.TryGetValue(path, out var state))
            _scriptInputs[path] = state = new AutomationScriptInput.FileState(path);
        state.Refresh(required);
        return state;
    }

    private AutomationTask ResolveTriggersLocked(AutomationTask task)
    {
        string? path;
        try { path = ScriptInputPath(task); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return task with { ManualEnabled = false, ScheduleEnabled = false,
                TriggerConfigurationError = "无法准备任务触发器配置：" + error.Message };
        }
        var state = path == null ? null : InputState(path, task.ScriptInputPath != null);
        if (_resolvedTriggers.TryGetValue(task.Id, out var cached) && ReferenceEquals(cached.Source, task)
            && cached.Path == path && ReferenceEquals(cached.Triggers, state?.Value) && cached.Error == state?.Error)
            return cached.Resolved;
        var resolved = task.TriggerSourcePath == null && task.TriggerSourceVersion == null && task.TriggerConfigurationError == null
            ? task : task with { TriggerSourcePath = null, TriggerSourceVersion = null, TriggerConfigurationError = null };
        if (state?.Error != null) resolved = resolved with { ManualEnabled = false, ScheduleEnabled = false,
            TriggerSourcePath = path, TriggerConfigurationError = state.Error };
        else if (state != null) resolved = state.Value is { } triggers ? triggers.Apply(resolved) with
            { TriggerSourcePath = path, TriggerSourceVersion = triggers.Version } : resolved with { TriggerSourceVersion = "" };
        _resolvedTriggers[task.Id] = (task, path, state?.Value, state?.Error, resolved);
        return resolved;
    }

    private IEnumerable<AutomationTask> EffectiveTasksLocked() => _document.Tasks.Select(ResolveTriggersLocked);

    private bool RefreshTriggersLocked()
    {
        var changed = !_triggersObserved || _observedTriggers.Count != _document.Tasks.Count;
        for (var index = 0; index < _document.Tasks.Count; index++)
        {
            var task = ResolveTriggersLocked(_document.Tasks[index]);
            if (index < _observedTriggers.Count)
            {
                var previous = _observedTriggers[index];
                if (!ReferenceEquals(task, previous) && (task.Id != previous.Id || task.ManualEnabled != previous.ManualEnabled ||
                    task.ScheduleEnabled != previous.ScheduleEnabled || task.Trigger != previous.Trigger ||
                    !(task.AdditionalTriggers ?? []).SequenceEqual(previous.AdditionalTriggers ?? []) ||
                    task.TriggerSourcePath != previous.TriggerSourcePath || task.TriggerConfigurationError != previous.TriggerConfigurationError))
                    changed = true;
                _observedTriggers[index] = task;
            }
            else _observedTriggers.Add(task);
        }
        if (_observedTriggers.Count > _document.Tasks.Count)
            _observedTriggers.RemoveRange(_document.Tasks.Count, _observedTriggers.Count - _document.Tasks.Count);
        if (changed)
        {
            var ids = _document.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _resolvedTriggers.Keys.Where(id => !ids.Contains(id)).ToArray()) _resolvedTriggers.Remove(id);
        }
        _triggersObserved = true;
        return changed;
    }

    private void SaveTaskConfigurationLocked(AutomationTask task, AutomationTask? saved)
    {
        var current = ResolveTriggersLocked(task);
        if (current.TriggerConfigurationError is { } error) throw new InvalidOperationException(error);
        if (task.TriggerSourceVersion != null && task.TriggerSourceVersion != current.TriggerSourceVersion)
            throw new InvalidOperationException("脚本触发器已变化，请重新打开任务后再保存");
        var path = current.TriggerSourcePath;
        var stored = path == null ? task : task with
        {
            // Preserve tasks.json as the fallback instead of copying the file override into it.
            Trigger = saved?.Trigger ?? new(), AdditionalTriggers = saved?.AdditionalTriggers, ScheduleEnabled = saved?.ScheduleEnabled ?? false,
            ManualEnabled = saved?.ManualEnabled ?? true
        };
        void Persist()
        {
            var tasks = _document.Tasks.ToList();
            var index = tasks.FindIndex(t => t.Id == stored.Id);
            if (index >= 0) tasks[index] = stored;
            else tasks.Add(stored);
            var document = _document with { Tasks = tasks };
            _store.Save(document);
            _document = document;
        }
        if (path != null && (task.ManualEnabled != current.ManualEnabled || task.ScheduleEnabled != current.ScheduleEnabled
            || !task.SameSchedules(current)))
            InputState(path, required: true).Save(task, Persist);
        else Persist();
    }
}
