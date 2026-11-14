using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>
/// Turns the steps a person chose to run into the stages and nodes the engine skips for that run.
/// Only top-level steps and the direct children of a stage are selectable; Finally and recovery always run.
/// </summary>
public static class AutomationManualSelection
{
    public const string SkipReason = "本次未选择运行";
    private const string StagePrefix = "stage:";

    public static PregelManualSkip ToSkip(IReadOnlyList<AutomationStep> steps, IReadOnlySet<string> selected)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(selected);
        var stages = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new HashSet<NodeId>();
        var chosen = 0;
        foreach (var step in steps)
        {
            if (step.IsFinally || step.IsRecovery) continue;
            if (selected.Contains(step.Key)) { chosen++; continue; }
            if (!step.IsStage) { Skip(step); continue; }
            var children = step.Children.Where(child => !child.IsFinally && !child.IsRecovery).ToArray();
            var picked = children.Count(child => selected.Contains(child.Key));
            chosen += picked;
            if (picked == 0) stages.Add(step.Key.StartsWith(StagePrefix, StringComparison.Ordinal) ? step.Key[StagePrefix.Length..] : step.Key);
            else foreach (var child in children.Where(child => !selected.Contains(child.Key))) Skip(child);
        }
        if (chosen == 0) throw new InvalidOperationException("请至少选择一个要运行的步骤");
        return new(nodes, stages, SkipReason);

        void Skip(AutomationStep step)
        {
            foreach (var node in step.NodeIds) nodes.Add(new NodeId(node));
        }
    }
}
