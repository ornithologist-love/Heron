using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using Heron.AutomationTenants.Contracts;
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Heron.Pregel.Avalonia.ViewModels;
public sealed partial class AutomationPageViewModel
{
    [ObservableProperty] private bool _showWorkflowUpload;
    [ObservableProperty] private AutomationPagePanel? _workflowUpload;
    [RelayCommand]
    private async Task OpenWorkflowUploadAsync()
    {
        if (!CanUploadWorkflow || SavedTask == null || SelectedScriptTemplateId == null) return;
        var task = _host.Tasks.FirstOrDefault(item => item.Task.Id == SavedTask.Id)?.Task;
        var path = _host.Scripts.SavedScriptPath(SelectedScriptTemplateId);
        if (task == null || path == null) { TaskScriptMessage = "请先选择已保存的 CSX 流程"; return; }
        WorkflowUpload = Extensions.CreateWorkflowUploadPanel!(_host, task, path);
        ShowWorkflowUpload = true;
        await WorkflowUpload.InitializeAsync();
    }
}
