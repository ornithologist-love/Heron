using System.Diagnostics;
using System.Text;

namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class RunOutputTests
{
    [Fact]
    public async Task CancellationBetweenNodes_PreservesCommittedOutputAndStopsNextNode()
    {
        using var cancel = new CancellationTokenSource();
        var tasks = new Tasks(cancel);
        var graph = tasks.Definition.Start(tasks.WriteAndCancel).Then(tasks.Next).Compile();
        var engine = new PregelEngine();
        var result = await graph.RunAsync(engine, cancellationToken: cancel.Token);
        Assert.Equal(PregelRunStatus.Cancelled, result.Status);
        Assert.Equal(42, graph.Read(result, tasks.Output));
        Assert.False(tasks.NextRan);
        Assert.Equal(PregelRunStatus.Cancelled, (await engine.GetStateAsync(result.RunId))!.Status);
    }

    [Fact]
    public async Task CancellationInsideNode_DoesNotCommitPartialWrites()
    {
        using var cancel = new CancellationTokenSource();
        var tasks = new Tasks(cancel);
        var graph = tasks.Definition.Start(tasks.CancelWithin).Compile();
        var result = await graph.RunAsync(cancel.Token);
        Assert.Equal(PregelRunStatus.Cancelled, result.Status);
        Assert.Equal(0, graph.Read(result, tasks.Output));
    }

    [Fact]
    public async Task CancellationWhileWaitingForRetry_ReturnsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var tasks = new Tasks(cancel);
        var graph = tasks.Definition.Start(tasks.Fail, new PregelRetryPolicy(3, TimeSpan.FromSeconds(30))).Compile();
        var observer = new Observer(value => { if (value.Kind == "node.finished") cancel.Cancel(); });
        var result = await graph.RunAsync(new PregelEngine(), new PregelRunOptions(Observer: observer), cancel.Token);
        Assert.Equal(PregelRunStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task LocalRunner_EmitsBeforeProcessExits_AndCancellationReapsProcess()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pregel-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var text = new StringBuilder();
            var runner = new LocalToolExecutionRunner();
            ToolExecution execution = OperatingSystem.IsWindows()
                ? NewPSExecution("[Console]::Write('ready'); Start-Sleep -Seconds 30")
                : NewBashExecution("printf ready; exec sleep 30");
            var task = runner.ExecuteAsync(execution,
                (chunk, source) => { if (source == "stdout") { text.Append(chunk); if (text.ToString().Contains("ready")) seen.TrySetResult(); } }, cancel.Token).AsTask();
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(task.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Tasks
    {
        private readonly CancellationTokenSource? _cancel;
        public PregelTaskGraphDsl Definition { get; } = Pregel.Define("output-test");
        public PregelVariable<int> Output { get; }
        public PregelVariable<int> Input { get; } = null!;
        public bool NextRan { get; private set; }
        public Tasks(CancellationTokenSource? cancel = null)
        {
            _cancel = cancel;
            Output = Definition.Output("output", 0);
            if (cancel == null) Input = Definition.Variable<int>("input");
        }
        public void WriteAndCancel(PregelNodeContext context) { context.Write(Output, 42); _cancel!.Cancel(); }
        public Task CancelWithin(PregelNodeContext context, CancellationToken token)
        {
            context.Write(Output, 42);
            _cancel!.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Next(PregelNodeContext context) { NextRan = true; context.Log("next"); }
        public void Copy(PregelNodeContext context) { context.Write(Output, context.Read(Input)); context.Log("copied"); }
        public void Fail(PregelNodeContext context) => throw new InvalidOperationException("retry me");
        public void Retry(PregelNodeContext context) { context.Log("attempt"); if (context.AttemptNumber == 1) throw new IOException(); }
    }

    private sealed class Observer(Action<PregelRunEvent>? callback = null) : IPregelRunObserver
    {
        public List<PregelRunEvent> Events { get; } = [];
        public void OnEvent(PregelRunEvent value) { Events.Add(value); callback?.Invoke(value); }
    }
}
