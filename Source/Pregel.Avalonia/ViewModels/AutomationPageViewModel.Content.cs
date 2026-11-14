namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    // Local type templates are resolved only for non-null Content; hidden bindings stay uninstantiated.
    public AutomationPageViewModel? ParametersContent => _pageActive && IsParametersTab && HasEditor ? this : null;
    public AutomationPageViewModel? HistoryContent => _pageActive && IsRunsTab ? this : null;
    public AutomationPageViewModel? LogsContent => _pageActive && IsRunsTab && IsRunReportExpanded && ShowRunLogs ? this : null;
    public AutomationPageViewModel? PlanContent => _pageActive && ShowRunArea && HasEditor && !HasRun && !HasSubgraph ? this : null;
    public AutomationPageViewModel? WorkflowContent => _pageActive && ShowRunArea && HasRun && !HasSubgraph && ShowWorkflowView ? this : null;
    public AutomationPageViewModel? SubgraphContent => _pageActive && ShowRunArea && HasSubgraph && ShowWorkflowView ? this : null;
    public AutomationPageViewModel? LaunchContent => _pageActive && ShowRunArea && ShowLaunchParameters ? this : null;
    public AutomationPageViewModel? ArtifactContent => _pageActive && ShowRunArea && ShowArtifactView ? this : null;

    private void NotifyDeferredContent(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || propertyName is nameof(IsParametersTab) or nameof(IsRunsTab) or nameof(ShowRunArea)
            or nameof(HasEditor) or nameof(HasRun) or nameof(HasSubgraph) or nameof(ShowWorkflowView)
            or nameof(ShowLaunchParameters) or nameof(ShowArtifactView) or nameof(IsRunReportExpanded) or nameof(ShowRunLogs))
            NotifyDeferredContent();
    }

    private void NotifyDeferredContent()
    {
        OnPropertyChanged(nameof(ParametersContent));
        OnPropertyChanged(nameof(HistoryContent));
        OnPropertyChanged(nameof(LogsContent));
        OnPropertyChanged(nameof(PlanContent));
        OnPropertyChanged(nameof(WorkflowContent));
        OnPropertyChanged(nameof(SubgraphContent));
        OnPropertyChanged(nameof(LaunchContent));
        OnPropertyChanged(nameof(ArtifactContent));
    }
}
