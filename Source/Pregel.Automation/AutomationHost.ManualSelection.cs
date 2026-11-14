namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    /// <summary>
    /// Queues a run of the saved task that executes only the chosen steps; the rest are recorded as skipped.
    /// The selection is saved with the run, so resuming or retrying it stays partial.
    /// </summary>
    public string StartSelectedRun(string taskId, IReadOnlySet<string> stepKeys)
    {
        ArgumentNullException.ThrowIfNull(stepKeys);
        AutomationTask? task;
        IAutomationTemplate? template = null;
        lock (_gate)
        {
            task = _document.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null) _templates.TryGetValue(task.TemplateId, out template);
        }
        if (task == null || template is not IAutomationGraphTemplate)
            throw new InvalidOperationException("只有 Graph 流程支持只运行所选步骤");
        var skip = AutomationManualSelection.ToSkip(template.PlanSteps(task), stepKeys);
        return StartRunCore(taskId, "manual", null, null, null, manualSkip: skip)
            ?? throw new InvalidOperationException("任务正在运行、待审核或已被占用，请稍后重试");
    }
}
