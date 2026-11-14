using System.Linq;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    private AutomationReexecutionViewModel? _reexecution;

    /// <summary>「继续运行」「从此节点重新运行」与来源/派生链接，见 <see cref="AutomationReexecutionViewModel"/>。</summary>
    public AutomationReexecutionViewModel Reexecution => _reexecution ??= new(_host, SelectRunById, OnReexecutionStarted);

    private void SelectRunById(string runId)
    {
        if (Runs.FirstOrDefault(r => r.RunId == runId) is { } run) SelectedRun = run;
    }

    private void OnReexecutionStarted(string runId)
    {
        _historyKey = null;
        Refresh();
        SelectRunById(runId);
    }

    private void NotifyReexecution() => Reexecution.Update(SavedTask?.Id, SelectedRun, Runs);
}
