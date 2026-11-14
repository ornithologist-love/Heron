using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Headless;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml.Styling;
using global::Avalonia.Threading;
using Heron.ClientControls.Dialogs;
using Heron.Pregel.Automation;
using Heron.Pregel.Avalonia.ViewModels;
using Heron.Pregel.Avalonia.Views;
using Semi.Avalonia;
using Xunit;

namespace Heron.Pregel.Avalonia.Tests;

[Anchor("Docs/pregel-avalonia.md#执行与页面生命周期")]
public sealed class AutomationWorkspaceTests
{
    [Fact]
    public void WorkspaceProjectsHostRuns_AndHidingOrDisposingItNeverStopsExecution()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new SemiTheme());
        Application.Current.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Heron.ClientControls/"))
            { Source = new Uri("avares://Heron.ClientControls/Themes/Controls.axaml") });
        Application.Current.Styles.Add(new StyleInclude(new Uri("avares://Heron.ClientControls/"))
            { Source = new Uri("avares://Heron.ClientControls/Themes/HeronTheme.axaml") });
        var root = Path.Combine(Path.GetTempPath(), "heron-ui-test-" + Guid.NewGuid().ToString("N"));
        var host = new AutomationHost(new AutomationStore(root), Path.Combine(root, "evidence"), true);
        var template = new BlockingTemplate();
        using var registration = host.RegisterTemplate(template);
        var page = new AutomationPageViewModel(host);
        var view = new AutomationPageView { DataContext = page };
        var window = new Window { Width = 1280, Height = 800, Content = view };
        try
        {
            window.Show();
            page.SetPageActive(true);
            Assert.False(page.HasGlobalSettings);
            Assert.False(page.CanUploadWorkflow);
            Assert.False(view.FindControl<Button>("GlobalSettingsButton")!.IsVisible);
            page.NewTaskCommand.Execute(page.Templates.Single());
            page.Editor!.Name = "Isolated mock task";
            page.Editor.ScheduleEnabled = true;
            var scheduledTime = DateTime.Now.AddHours(6).ToString("HH:mm");
            page.Editor.Schedules[0].Time = scheduledTime;
            page.SaveSettingsCommand.Execute(null);
            Assert.Single(host.Tasks);
            Assert.True(host.Tasks[0].Task.ScheduleEnabled);
            Assert.Equal(scheduledTime, host.Tasks[0].Task.Trigger.DailyTime);
            var taskId = page.SelectedTask!.Id;
            page.RunSelectedCommand.Execute(null);
            Until(() => template.Started.Task.IsCompleted);
            page.SetPageActive(false);
            Assert.Null(page.ParametersContent);
            Assert.Null(page.HistoryContent);
            var projected = page.SelectedRun!.Status;
            template.Release.TrySetResult();
            Until(() => host.History(taskId).FirstOrDefault()?.IsFinished == true);
            Assert.Equal(AutomationRunStatus.Succeeded, host.History(taskId)[0].Status);
            Assert.Equal(projected, page.SelectedRun.Status);
            page.SetPageActive(true);
            Assert.Equal(AutomationRunStatus.Succeeded, page.SelectedRun.Status);

            template.Reset();
            page.RunSelectedCommand.Execute(null);
            Until(() => template.Started.Task.IsCompleted);
            page.Dispose();
            Assert.False(host.History(taskId)[0].IsFinished);
            template.Release.TrySetResult();
            Until(() => host.History(taskId)[0].IsFinished);
            Assert.Equal(2, host.History(taskId).Count);
            var logRoot = Path.Combine(root, "mock-log");
            Directory.CreateDirectory(logRoot);
            File.WriteAllText(Path.Combine(logRoot, "run.log"), "mock evidence line\n");
            using var logs = new AutomationLogsViewModel(() => Task.CompletedTask);
            logs.SetRun(host.History(taskId)[0] with { EvidenceDirectories = [logRoot] });
            logs.SetActive(true);
            Until(() => logs.Document.Text.Contains("mock evidence line"));
            logs.SetActive(false);
            using var restored = new AutomationPageViewModel(host);
            restored.SetPageActive(true);
            restored.SelectedTask = restored.Tasks.Single();
            Assert.Equal(2, restored.Runs.Count);
            template.Reset();
            restored.RunSelectedCommand.Execute(null);
            Until(() => template.Started.Task.IsCompleted);
            restored.CancelSelectedCommand.Execute(null);
            Until(() => host.History(taskId)[0].IsFinished);
            Assert.Equal(AutomationRunStatus.Cancelled, host.History(taskId)[0].Status);

            var content = new TextBox();
            AutomationPagePanel? panel = null;
            using var contributed = new AutomationPageViewModel(host, new AutomationPageExtensions
            {
                CreateSettingsPanel = () => panel = new AutomationPagePanel(content, initialFocus: content)
            });
            view.DataContext = contributed;
            Assert.True(contributed.HasGlobalSettings);
            view.FindControl<Button>("GlobalSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = view.FindControl<ModalDialog>("GlobalSettingsDialog")!;
            Assert.Same(content, dialog.Content);
            panel!.Close(true);
            Assert.False(dialog.IsOpen);
            LocalScriptLoadsThroughTheWorkspace_AndReviewPreservesRunIdentityAndArtifact();
        }
        finally
        {
            page.Dispose(); window.Close();
            Wait(host.DisposeAsync().AsTask());
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void LocalScriptLoadsThroughTheWorkspace_AndReviewPreservesRunIdentityAndArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-ui-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "Review.csx");
        File.WriteAllText(script, """
            return new MockReview();
            sealed class MockInput { [DisplayName("Message")] public string Message { get; init; } = "isolated mock"; }
            [TemplateId("mock.script.review")]
            sealed class MockReview : AutomationReviewTemplate<MockInput>
            {
                protected override IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, MockInput input) => [];
                protected override Task<AutomationRunResult> RunAsync(AutomationRunContext context, MockInput input, CancellationToken token)
                {
                    context.Log(input.Message);
                    return Task.FromResult(AutomationRunResult.AwaitingReview(
                        new AutomationReview("mock-review", 1, "Confirm", input.Message, "mock-hash", DateTimeOffset.UtcNow)));
                }
                protected override Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, MockInput input,
                    AutomationReview review, AutomationReviewDecision decision, CancellationToken token)
                {
                    var directory = Path.Combine(context.StateRoot, "mock-output");
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, "report.md");
                    File.WriteAllText(path, "# " + input.Message);
                    return Task.FromResult(AutomationRunResult.Succeeded("approved") with { ArtifactPath = path });
                }
            }
            """);
        var host = new AutomationHost(new AutomationStore(Path.Combine(root, "store")), Path.Combine(root, "evidence"), true);
        AutomationPageViewModel? page = null;
        try
        {
            Wait(host.Scripts.OpenDirectoryAsync(root));
            Wait(host.Scripts.LoadAsync(script));
            page = new AutomationPageViewModel(host);
            page.SetPageActive(true);
            page.NewTaskCommand.Execute(page.Templates.Single());
            page.Editor!.Name = "Mock script review";
            page.Editor.Fields.Single().Value = "edited mock";
            page.SaveSettingsCommand.Execute(null);
            var taskId = page.SelectedTask!.Id;
            page.RunSelectedCommand.Execute(null);
            Until(() => host.History(taskId).FirstOrDefault()?.Status == AutomationRunStatus.AwaitingReview);
            page.SetPageActive(false); page.SetPageActive(true);
            Assert.True(page.CanReviewSelected);
            var runId = page.SelectedRun!.RunId;
            page.ApproveDraftCommand.Execute(null);
            Until(() => host.FindRun(taskId, runId)?.Status == AutomationRunStatus.Succeeded);
            page.SetPageActive(false); page.SetPageActive(true);
            Assert.Equal(runId, page.SelectedRun!.RunId);
            Assert.Single(host.History(taskId));
            Until(() => !page.Artifacts.IsLoading && !page.Artifacts.IsPreviewLoading);
            Assert.Contains("edited mock", page.Artifacts.PreviewText);
            Assert.EndsWith("report.md", host.FindRun(taskId, runId)!.ArtifactPath);
        }
        finally
        {
            page?.Dispose(); Wait(host.DisposeAsync().AsTask());
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > timeout) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(10);
        }
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    public sealed class Input { }
    [TemplateId("mock.workspace")]
    private sealed class BlockingTemplate : AutomationTemplate<Input>
    {
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        protected override IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, Input input) => [];
        protected override async Task<AutomationRunResult> RunAsync(AutomationRunContext context, Input input, CancellationToken token)
        {
            context.Log("Isolated execution"); Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return AutomationRunResult.Succeeded("done");
        }
    }
}
