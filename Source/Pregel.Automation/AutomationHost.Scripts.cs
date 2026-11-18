using System.Reflection;

namespace Heron.Pregel.Automation;

public sealed class AutomationScriptInUseException() : InvalidOperationException(
    "此脚本有正在运行或等待审核的任务，请完成或停止后再重新加载/移除");

public sealed partial class AutomationHost
{
    private readonly Dictionary<Guid, Assembly[]> _scriptReferences = [];
    private readonly HashSet<string> _sourceReloadRequired = new(StringComparer.Ordinal);

    public IDisposable RegisterScriptReferences(params Assembly[] references)
    {
        EnsureActive();
        var id = Guid.NewGuid();
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("自动化正在停止");
            _scriptReferences.Add(id, references.Distinct().ToArray());
        }
        return new Registration(() => { lock (_gate) _scriptReferences.Remove(id); });
    }

    public Assembly[] ScriptReferences
    {
        get { lock (_gate) return _scriptReferences.Values.SelectMany(value => value).Distinct().ToArray(); }
    }

    internal void EnsureScriptCanChange(string id)
    {
        lock (_gate)
        {
            if (_stopped) throw new InvalidOperationException("自动化正在停止");
            // Restore a missing registration so persisted reviews can be resumed after restart.
            // Pending reviews protect an existing definition from replacement/removal only.
            if (_active.Values.Any(run => run.Task.TemplateId == id)
                || (_templates.ContainsKey(id)
                    && _document.Tasks.Any(task => task.TemplateId == id && (HasPendingReview(task.Id) || IsRunning(task.Id)))))
                throw new AutomationScriptInUseException();
        }
    }

    internal void CommitScriptChange(AutomationScriptSlot slot, AutomationScriptInstance? next, Action<Action> commit)
    {
        lock (_gate)
        {
            EnsureScriptCanChange(slot.Id);
            _templates.TryGetValue(slot.Id, out var registered);
            if (!ReferenceEquals(registered, slot.RegisteredTemplate) && !_sourceReloadRequired.Contains(slot.Id))
                throw new InvalidOperationException("脚本模板标识已被占用");
            // Persistence runs before publication, while execution and replacement are excluded.
            // The catalog also holds its projection lock until publication is complete.
            commit(() =>
            {
                if (next is null) _templates.Remove(slot.Id);
                else _templates[slot.Id] = next.Template;
                _sourceReloadRequired.Remove(slot.Id);
            });
        }
    }

    internal void ScriptChangeCommitted()
    {
        try { Reschedule(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] script change committed; scheduling/notification failed"); }
    }

    internal void CommitScriptSourceChange(IEnumerable<string> ids, Action commit)
    {
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            var affected = ids.ToArray();
            foreach (var id in affected) EnsureScriptCanChange(id);
            commit();
            // Old compiled templates may open Python/Shared files later. Do not run them against a new source tree.
            foreach (var id in affected)
            {
                _templates.Remove(id);
                _sourceReloadRequired.Add(id);
            }
        }
        ScriptChangeCommitted();
    }

    internal void DetachScript(AutomationScriptSlot slot)
    {
        // Shutdown preserves restore records; the slot still owns instance disposal.
        lock (_gate)
            if (_templates.TryGetValue(slot.Id, out var current) && ReferenceEquals(current, slot.RegisteredTemplate))
                _templates.Remove(slot.Id);
        ScriptChangeCommitted();
    }
}
