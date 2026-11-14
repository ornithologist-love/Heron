using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    public bool CanTransferToChat => CanReviewSelected && _host.CanOpenChat &&
        (SelectedRun?.Review?.ChatSession != null || (SavedTask is { } task &&
            _host.Tasks.FirstOrDefault(item => item.Task.Id == task.Id)?.Template is IAutomationReviewTemplate { SupportsChatTransfer: true }));

    [RelayCommand]
    private void TransferToChat()
    {
        if (CanTransferToChat) SubmitReview(AutomationReviewAction.TransferToChat);
    }
}
