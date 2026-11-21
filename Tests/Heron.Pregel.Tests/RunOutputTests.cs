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
        var graph = Pregel.Define<int>("output-test").Then(tasks.WriteAndCancel).Then(tasks.Next).Compile();
        var engine = new PregelEngine();
        var result = await graph.Prepare(0, new() { Engine = engine }).RunAsync(cancel.Token);
        Assert.Equal(PregelRunStatus.Cancelled, result.Status);
        Assert.Equal(42, graph.Read(result));
        Assert.False(tasks.NextRan);
        Assert.Equal(PregelRunStatus.Cancelled, (await engine.GetStateAsync(result.RunId))!.Status);
    }

    [Fact]
    public async Task CancellationInsideNode_DoesNotCommitPartialWrites()
    {
        using var cancel = new CancellationTokenSource();
        var builder = new PregelGraphBuilder("cancel-pending-output");
        var output = builder.Output("output", 0);
        var node = builder.Node("cancel-with-write", (context, token) =>
        {
            context.Write(output, 42);
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        var graph = builder.SetEntry(node).Compile();
        var result = await new PregelEngine().StartAsync(graph, cancel.Token);
        Assert.Equal(PregelRunStatus.Cancelled, result.Status);
        Assert.Equal(0, graph.Read(result, output));
    }

    [Fact]
    public async Task CancellationWhileWaitingForRetry_ReturnsCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var tasks = new Tasks(cancel);
        var graph = Pregel.Define("retry-cancellation").Then(tasks.Fail)
            .Retry(new(3, TimeSpan.FromSeconds(30))).Compile();
        var observer = new Observer(value => { if (value.Kind == "node.finished") cancel.Cancel(); });
        var result = await graph.RunAsync(PregelUnit.Value, new(Observer: observer), cancel.Token);
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

    private sealed class Tasks(CancellationTokenSource? cancel = null)
    {
        public bool NextRan { get; private set; }

        public int WriteAndCancel(int value, PregelNodeContext context, CancellationToken token)
        {
            cancel!.Cancel();
            return 42;
        }

        public Task Next(int value, PregelNodeContext context, CancellationToken token)
        {
            NextRan = true;
            context.Log("next");
            return Task.CompletedTask;
        }
        public void Fail(PregelUnit _, PregelNodeContext context, CancellationToken token) =>
            throw new InvalidOperationException("retry me");
    }

    private sealed class Observer(Action<PregelRunEvent>? callback = null) : IPregelRunObserver
    {
        public List<PregelRunEvent> Events { get; } = [];
        public void OnEvent(PregelRunEvent value) { Events.Add(value); callback?.Invoke(value); }
    }
}
