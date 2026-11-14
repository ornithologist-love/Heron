using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System.Collections.Generic;

using Heron.Pregel;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationReexecutionViewModel
{
    // For read-only design previews. The Gallery host never registers executable templates, so entries are seeded directly.
    public void SetLayoutPreview(AutomationRunItemViewModel run, IReadOnlyList<AutomationRunItemViewModel> runs, string? problem)
    {
        _run = run;
        UpdateLineage(run.Record, runs);
        var source = run.Record.SourceRunId == null;
        ShowContinue = source;
        CanContinue = source;
        ContinueToolTip = ContinueTip;
        MarkRerunnable(run.Steps, true);
        if (!source) return;
        _pending = new(PregelReexecutionMode.Continue, []);
        IsConfirmOpen = true;
        ConfirmTitle = "继续运行？";
        if (problem != null) ConfirmProblem = problem;
        else ConfirmSummary = "复用 2 个已完成步骤的结果；从失败或取消处继续执行。";
    }
}
