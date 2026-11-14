using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    public AutomationPermission? ToolPermission => !IsSelectedTaskInvalid && SavedTask is { } task && SelectedRun is { } run
        ? _host.PendingPermission(task.Id, run.RunId) : null;
    public bool HasToolPermission => ToolPermission != null;
    [RelayCommand] private void AllowTool() => AnswerTool(true);
    [RelayCommand] private void DenyTool() => AnswerTool(false);
    private void AnswerTool(bool allow)
    {
        if (SavedTask is not { } task || SelectedRun is not { } run || ToolPermission is not { } permission) return;
        try { _host.AnswerPermission(task.Id, run.RunId, permission.Id, allow); }
        catch (Exception ex) { ReviewError = ex.Message; }
    }
    [ObservableProperty] private string _reviewFeedback = "";
    private string? _submittedReviewId;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReviewError))] private string _reviewError = "";
    [ObservableProperty] private bool _showReviewSettings;
    public bool HasSelectedReview => SelectedRun?.HasReview == true;
    public bool CanViewReviewSettings => !IsSelectedTaskInvalid && HasEditor && HasSelectedReview;
    public bool CanReviseSelected => CanReviewSelected && SelectedRun?.Review?.CanRevise == true;
    public string ReviewApproveLabel => SelectedRun?.Review?.ApproveLabel is "批准并保存"
        ? "接受并归档" : SelectedRun?.Review?.ApproveLabel ?? "批准并继续";
    public string ReviewReviseLabel => SelectedRun?.Review?.ReviseLabel ?? "带意见继续";
    public string ReviewFeedbackPlaceholder => SelectedRun?.Review?.FeedbackPlaceholder ?? "补充要求、指出问题，或提出下一轮问题";
    public string ReviewHint => SelectedRun?.Review?.Hint ?? "";
    public bool ShowEditor => ShowSettingsDocked;
    public global::Avalonia.Controls.GridLength SettingsColumnWidth => new(ShowSettingsDocked ? 360 : 0);
    public global::Avalonia.Controls.GridLength SettingsSpacingWidth => new(ShowSettingsDocked ? 14 : 0);
    public string ReviewSettingsButtonText => "查看参数设置";
    partial void OnShowReviewSettingsChanged(bool value)
    {
        if (value) IsSettingsExpanded = true;
        NotifyReviewLayout();
    }
    [RelayCommand]
    private void ToggleReviewSettings()
    {
        if (CanViewReviewSettings) { ShowReviewSettings = true; PageTabIndex = 0; }
    }
    [RelayCommand]
    private void ViewReviewArtifacts()
    {
        if (SelectedRun is not { HasArtifact: true } run) return;
        Artifacts.SetRun(run.Record);
        RunViewIndex = 2;
    }
    private void NotifyReviewLayout()
    {
        NotifyReexecution();
        OnPropertyChanged(nameof(ToolPermission));
        OnPropertyChanged(nameof(HasToolPermission));
        OnPropertyChanged(nameof(HasSelectedReview));
        OnPropertyChanged(nameof(CanViewReviewSettings));
        OnPropertyChanged(nameof(CanReviseSelected));
        OnPropertyChanged(nameof(CanTransferToChat));
        OnPropertyChanged(nameof(ReviewApproveLabel));
        OnPropertyChanged(nameof(ReviewReviseLabel));
        OnPropertyChanged(nameof(ReviewFeedbackPlaceholder));
        OnPropertyChanged(nameof(ReviewHint));
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(SettingsColumnWidth));
        OnPropertyChanged(nameof(SettingsSpacingWidth));
        OnPropertyChanged(nameof(ReviewSettingsButtonText));
        OnPropertyChanged(nameof(ShowSettingsDocked));
        OnPropertyChanged(nameof(ShowSettingsOverlay));
        OnPropertyChanged(nameof(ShowSettingsRail));
        OnPropertyChanged(nameof(ShowSettingsCard));
    }
    public bool HasReviewError => !string.IsNullOrEmpty(ReviewError);
    public bool CanReviewSelected => !IsSelectedTaskInvalid && SavedTask is { } task
        && SelectedRun is { Status: AutomationRunStatus.AwaitingReview, Review: not null } run
        && (task.AllowParallel || !task.IsActive)
        && _host.Tasks.FirstOrDefault(t => t.Task.Id == task.Id)?.Template is IAutomationReviewTemplate;

    [RelayCommand] private void ApproveDraft() => SubmitReview(AutomationReviewAction.Approve);
    [RelayCommand] private void ReviseDraft() => SubmitReview(AutomationReviewAction.Revise);
    [RelayCommand] private void AbandonDraft() => SubmitReview(AutomationReviewAction.Abandon);

    private void SubmitReview(AutomationReviewAction action)
    {
        if (!CanReviewSelected || SavedTask is not { } task || SelectedRun is not { Review: { } review } run) return;
        ReviewError = "";
        try
        {
            _host.Review(task.Id, run.RunId, new(review.Id, review.ContentHash, action, ReviewFeedback.Trim()));
            _submittedReviewId = action == AutomationReviewAction.Revise ? review.Id : null;
            _historyKey = null;
            Refresh();
        }
        catch (Exception ex) { ReviewError = ex.Message; }
    }

    private void SynchronizeReviewFeedback()
    {
        if (_submittedReviewId == null || SelectedRun?.Review?.Id == _submittedReviewId) return;
        // Clear only after a new result arrived. A failed continuation keeps the user's input for retry.
        ReviewFeedback = "";
        _submittedReviewId = null;
    }
}
