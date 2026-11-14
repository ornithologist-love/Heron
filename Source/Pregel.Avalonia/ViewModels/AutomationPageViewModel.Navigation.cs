using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed record AutomationParameterNavigationItem(string Key, string Title);

public sealed partial class AutomationPageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersTab), nameof(IsPlanTab), nameof(IsRunsTab), nameof(ShowRunArea), nameof(RunAreaEmptyText), nameof(ShowSaveActions))]
    private int _pageTabIndex;
    private bool _updatingHistory;
    private AutomationScriptEntry? _selectedScriptEntry;
    private CancellationTokenSource? _scriptInspection;
    public bool IsParametersTab => PageTabIndex == 0;
    public bool IsPlanTab => PageTabIndex == 1;
    public bool IsRunsTab => PageTabIndex == 2;
    public bool ShowRunArea => !IsParametersTab;
    public ObservableCollection<AutomationParameterNavigationItem> ParameterSections { get; } = [];
    [ObservableProperty] private AutomationParameterNavigationItem? _selectedParameterSection;

    partial void OnPageTabIndexChanged(int value)
    {
        ShowPlan = value == 1;
        CloseSubgraph();
        if (value == 2) SelectedRun ??= Runs.FirstOrDefault();
        NotifySettingsLayout();
    }

    private void SelectInitialPage()
    {
        PageTabIndex = IsDraft ? 0 : IsSelectedTaskInvalid ? 2
            : SelectedTask is { Availability: AutomationTaskAvailability.TemplateNotLoaded or AutomationTaskAvailability.TemplateLoadFailed
                or AutomationTaskAvailability.TemplateNeedsReload }
                || _selectedScriptEntry is { NeedsDownload: true } or { NeedsReload: true } ? 1 : HasRuns ? 2 : 0;
        ShowPlan = IsPlanTab;
        RefreshParameterSections();
        SelectedParameterSection = ParameterSections.FirstOrDefault();
    }

    private void RefreshParameterSections()
    {
        var groups = Editor?.VisibleParameterGroups.Select(group => new AutomationParameterNavigationItem(
            "parameter:" + group.Title, group.HasTitle ? group.Title : "参数")) ?? [];
        SynchronizeList(ParameterSections, new[]
        {
            new AutomationParameterNavigationItem(GroupSchedule, "触发器"),
            new AutomationParameterNavigationItem(GroupTask, "任务")
        }.Concat(groups).Concat(new[]
        {
            new AutomationParameterNavigationItem("behavior", "运行行为"),
            new AutomationParameterNavigationItem("notification", "结果通知")
        }).ToArray());
        if (SelectedParameterSection == null || !ParameterSections.Contains(SelectedParameterSection))
            SelectedParameterSection = ParameterSections.FirstOrDefault();
    }

    private string? SelectedScriptTemplateId => Editor?.TemplateId
        ?? _host.Tasks.FirstOrDefault(view => view.Task.Id == SelectedTask?.Id)?.Task.TemplateId;

    private async Task DetectSelectedScriptChangeAsync()
    {
        _scriptInspection?.Cancel();
        _scriptInspection?.Dispose();
        _scriptInspection = null;
        var taskId = SavedTask?.Id;
        var templateId = SelectedScriptTemplateId;
        if (taskId == null || templateId == null) return;
        var path = _host.Scripts.LoadedScriptPath(templateId) ?? _host.Scripts.SavedScriptPath(templateId);
        if (path == null) return;
        var inspection = _scriptInspection = new CancellationTokenSource();
        var token = inspection.Token;
        try
        {
            var local = await _host.Scripts.ScanAsync(Path.GetDirectoryName(path)!, token);
            token.ThrowIfCancellationRequested();
            if (SavedTask?.Id != taskId) return;
            Apply(local.FirstOrDefault(entry => entry.TemplateId == templateId));
            if (AutomationScriptCatalog.RemoteDirectory is not { } remoteDirectory) return;
            var remote = await _host.Scripts.ScanAsync(remoteDirectory, token);
            token.ThrowIfCancellationRequested();
            if (SavedTask?.Id != taskId) return;
            Apply(remote.FirstOrDefault(entry => entry.TemplateId == templateId));
        }
        catch (OperationCanceledException) when (inspection.IsCancellationRequested) { }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "[Automation] inspect selected script revision failed");
        }

        void Apply(AutomationScriptEntry? entry)
        {
            if (entry == null) return;
            if (entry.NeedsDownload || entry.NeedsReload || !entry.IsLoaded && _host.Scripts.LoadedScriptPath(templateId) == null)
            {
                _selectedScriptEntry = entry;
                TaskScriptMessage = entry.Status + (entry.SourceInfo.Length > 0 ? " · " + entry.SourceInfo : "");
                PageTabIndex = 1;
                ShowPlan = true;
            }
            NotifyTaskScriptReload();
        }
    }

    [RelayCommand]
    private void ToggleRunLogDrawer()
    {
        if (IsRunReportExpanded) IsRunReportExpanded = false;
        else if (SelectedRun != null) OpenEvidence(SelectedRun);
    }
}
