using System;
using System.Linq;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationPageView
{
    private static T? FindContent<T>(Control host, string? name = null) where T : Control =>
        host.GetVisualDescendants().OfType<T>().FirstOrDefault(control => name == null || control.Name == name);

    private ScrollViewer? SettingsScroll => FindContent<ScrollViewer>(ParametersHost, "DeferredSettingsScroll");
    private Control? ParameterContent => FindContent<Control>(ParametersHost, "DeferredParameterContent");
    private ItemsControl? ParameterGroups => FindContent<ItemsControl>(ParametersHost, "DeferredParameterGroups");
    private Control? GroupSchedule => FindContent<Control>(ParametersHost, "DeferredGroupSchedule");
    private Control? GroupTask => FindContent<Control>(ParametersHost, "DeferredGroupTask");
    private Control? GroupOptions => FindContent<Control>(ParametersHost, "DeferredGroupOptions");
    private Control? GroupBehavior => FindContent<Control>(ParametersHost, "DeferredGroupBehavior");
    private Control? GroupNotification => FindContent<Control>(ParametersHost, "DeferredGroupNotification");
    private Control? ReviewPanel => FindContent<Control>(WorkflowHost, "DeferredReviewPanel");
    private AutomationLogsView? RunLogs => FindContent<AutomationLogsView>(RunLogsHost);

    private void ParametersContent_Attached(object? sender, VisualTreeAttachmentEventArgs e) =>
        Dispatcher.UIThread.Post(FocusParameterSection, DispatcherPriority.Loaded);

    private void RunLogs_Attached(object? sender, VisualTreeAttachmentEventArgs e) => UpdateRunLogHeight();

    private void UpdateRunLogHeight()
    {
        if (RunLogs is { } logs)
            logs.Height = Math.Max(120, Math.Min(520, RunDetailArea.Bounds.Height * 0.45));
    }
}
