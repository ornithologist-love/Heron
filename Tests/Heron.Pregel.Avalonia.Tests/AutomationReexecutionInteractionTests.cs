using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using Heron.Pregel.Avalonia.ViewModels;
using Heron.Pregel;
using Xunit;

namespace Heron.Pregel.Avalonia.Tests;

[Anchor("Docs/pregel-avalonia.md#执行与页面生命周期")]
public sealed class AutomationReexecutionInteractionTests
{
    [Fact]
    public async Task CancellingPendingPreflight_DiscardsItsResult_AndDoesNotCreateRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "pregel-preflight-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AutomationStore(root);
            await using var host = new AutomationHost(store, Path.Combine(root, "evidence"), true);
            var template = new PreviewTemplate();
            host.RegisterTemplate(template);
            var task = new AutomationTask { Id = "test", Name = "test", TemplateId = template.Id, Trigger = new AutomationTrigger("03:00") };
            host.Save(task);
            var source = new AutomationRunRecord { RunId = "source", TaskId = task.Id, TaskSnapshot = task, Status = AutomationRunStatus.Failed };
            store.Upsert(source);
            var view = new AutomationReexecutionViewModel(host, _ => { }, _ => throw new Exception("cancelled confirmation must not start"));
            var run = new AutomationRunItemViewModel(source);
            view.Update(task.Id, run, [run]);
            var checking = view.RequestContinueCommand.ExecuteAsync(null);
            await template.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(view.IsChecking);
            Assert.False(view.CanConfirm);
            view.CancelConfirmCommand.Execute(null);
            template.Preview.TrySetResult(new(new("point"), 0, [], []));
            await checking.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(view.IsConfirmOpen);
            Assert.False(view.CanConfirm);
            Assert.Single(host.History(task.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RerunEntry_OnlyTargetsScheduledNodesOwnedBySelectedRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "pregel-entry-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AutomationStore(root);
            await using var host = new AutomationHost(store, Path.Combine(root, "evidence"), true);
            var template = new PreviewTemplate();
            host.RegisterTemplate(template);
            var task = new AutomationTask { Id = "test", Name = "test", TemplateId = template.Id, Trigger = new AutomationTrigger("03:00") };
            host.Save(task);
            var own = new AutomationStepRecord { Key = "own", NodeIds = ["own"], State = AutomationStepState.Succeeded };
            var source = new AutomationRunRecord { RunId = "source", TaskId = task.Id, TaskSnapshot = task, Status = AutomationRunStatus.Failed,
                Steps = [own, own with { Key = "pending", State = AutomationStepState.Pending }, own with { Key = "reused", IsReused = true }, own with { Key = "finally", IsFinally = true }] };
            store.Upsert(source);
            var view = new AutomationReexecutionViewModel(host, _ => { }, _ => { });
            var run = new AutomationRunItemViewModel(source);
            view.Update(task.Id, run, [run]);
            Assert.True(run.Steps.Single(s => s.Key == "own").CanRerun);
            Assert.All(run.Steps.Where(s => s.Key != "own"), s => Assert.False(s.CanRerun));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public sealed class Input { }
    [TemplateId("test.preflight-interaction")]
    private sealed class PreviewTemplate : AutomationTemplate<Input>, IAutomationReexecutionTemplate
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PregelReexecutionPreview> Preview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsReexecution => true;
        public Task<PregelReexecutionPreview> PreviewReexecutionAsync(string sourceRunId, PregelReexecutionRequest request, CancellationToken token)
        { Requested.TrySetResult(); return Preview.Task.WaitAsync(token); }
        protected override IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, Input input) => [];
        protected override Task<AutomationRunResult> RunAsync(AutomationRunContext context, Input input, CancellationToken token) =>
            throw new Exception("preview fixture must never execute");
    }
}
