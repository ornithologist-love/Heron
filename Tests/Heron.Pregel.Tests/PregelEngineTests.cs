namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class PregelEngineTests
{
    [Fact]
    public async Task LinearGraph_RunsP4RevertThenSyncLatest_InSeparateSupersteps()
    {
        var runner = new RecordingToolExecutionRunner();
        var tasks = new UserP4Tasks();
        var graph = Pregel
            .Define("p4-revert-sync")
            .Start(tasks.MyP4Revert)
            .Then(tasks.MyP4SyncLatest, "MyPath")
            .Compile();

        var result = await graph.RunAsync(runner);

        result.Status.Should().Be(PregelRunStatus.Completed);
        runner.Executions.Should().Equal(
            NewBashExecution("p4 -c heron-workspace revert //..."),
            NewPSExecution("p4 -c heron-workspace sync //MyPath/...#head"));
        result.Checkpoint.Superstep.Should().Be(2);
    }

    [Fact]
    public async Task ToolExecution_NonZeroExitCodeFailsNode_AndStopsFollowingTask()
    {
        var runner = new RecordingToolExecutionRunner { ExitCode = 17 };
        var tasks = new UserP4Tasks();
        var graph = Pregel
            .Define("p4-tool-failure")
            .Start(tasks.MyP4Revert)
            .Then(tasks.MyP4SyncLatest, "MyPath")
            .Compile();

        var result = await graph.RunAsync(runner);

        result.Status.Should().Be(PregelRunStatus.Failed);
        result.Error!.Code.Should().Be("tool_execution_failed");
        runner.Executions.Should().ContainSingle()
            .Which.Should().BeOfType<BashExecution>();
    }

    [Fact]
    public async Task Barrier_MakesWritesVisibleOnlyToNextSuperstep()
    {
        var observed = -1;
        var builder = new PregelGraphBuilder("barrier-visibility");
        var value = builder.LastValueChannel("value", 0);
        var writer = builder.Node("writer", (context, _) =>
        {
            context.Read(value).Should().Be(0);
            context.Write(value, 42);
            context.Read(value).Should().Be(0);
            return ValueTask.CompletedTask;
        });
        var reader = builder.Node("reader", (context, _) =>
        {
            observed = context.Read(value);
            return ValueTask.CompletedTask;
        });
        var graph = builder.SetEntry(writer).AddEdge(writer, reader).Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Be(42);
    }

    [Fact]
    public async Task FailedInvocation_DoesNotCommitItsWrites()
    {
        var builder = new PregelGraphBuilder("discard-failed-writes");
        var value = builder.LastValueChannel("value", 7);
        var failing = builder.Node("failing", (context, _) =>
        {
            context.Write(value, 99);
            throw new InvalidOperationException("boom");
        });
        var graph = builder.SetEntry(failing).Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Failed);
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("node_exception");
        var encoded = result.Checkpoint.ChannelValues[value.Id];
        CheckpointCodecs.Int32.Decode(encoded.Data).Should().Be(7);
    }

    [Fact]
    public async Task Retry_KeepsLogicalInvocationId_AndChangesAttemptId()
    {
        var calls = new List<(PregelInvocationId Invocation, PregelAttemptId Attempt, int Number)>();
        var builder = new PregelGraphBuilder("retry");
        var node = builder.Node(
            "flaky",
            (context, _) =>
            {
                calls.Add((context.InvocationId, context.AttemptId, context.AttemptNumber));
                if (context.AttemptNumber == 1)
                {
                    throw new PregelException("transient", "try again");
                }

                return ValueTask.CompletedTask;
            },
            new PregelRetryPolicy(MaxAttempts: 2, RetryableErrorCodes: new HashSet<string> { "transient" }));
        var graph = builder.SetEntry(node).Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Completed);
        calls.Select(call => call.Number).Should().Equal(1, 2);
        calls[0].Invocation.Should().Be(calls[1].Invocation);
        calls[0].Attempt.Should().NotBe(calls[1].Attempt);
    }

    [Fact]
    public async Task Interrupt_ResumesSameLogicalInvocation_WithNewAttemptAndTypedValue()
    {
        var calls = new List<(PregelInvocationId Invocation, PregelAttemptId Attempt, int Number)>();
        string? accepted = null;
        var builder = new PregelGraphBuilder("interrupt-resume");
        var node = builder.Node("approval", (context, _) =>
        {
            calls.Add((context.InvocationId, context.AttemptId, context.AttemptNumber));
            if (!context.HasResumeValue)
            {
                context.Interrupt("waiting", 123);
                return ValueTask.CompletedTask;
            }

            accepted = context.ResumeValue<string>();
            return ValueTask.CompletedTask;
        });
        var graph = builder.SetEntry(node).Compile();
        var engine = new PregelEngine();

        var interrupted = await engine.StartAsync(graph);
        var wrongResume = async () => await engine.ResumeWithValueAsync(graph, interrupted.RunId, "wrong", "continue");
        await wrongResume.Should().ThrowAsync<PregelException>()
            .Where(exception => exception.Code == "interrupt_id_mismatch");
        var completed = await engine.ResumeWithValueAsync(
            graph,
            interrupted.RunId,
            interrupted.Interrupt!.InterruptId,
            "continue");

        interrupted.Status.Should().Be(PregelRunStatus.Interrupted);
        completed.Status.Should().Be(PregelRunStatus.Completed);
        accepted.Should().Be("continue");
        calls.Select(call => call.Number).Should().Equal(1, 2);
        calls[0].Invocation.Should().Be(calls[1].Invocation);
        calls[0].Attempt.Should().NotBe(calls[1].Attempt);
    }

    private sealed class RecordingToolExecutionRunner : IToolExecutionRunner
    {
        public List<ToolExecution> Executions { get; } = [];
        public int ExitCode { get; init; }

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            ToolExecution execution,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions.Add(execution);
            return ValueTask.FromResult(new ToolExecutionResult(ExitCode, "", ""));
        }
    }

    private sealed class UserP4Tasks
    {
        public BashExecution MyP4Revert() =>
            NewBashExecution("p4 -c heron-workspace revert //...");

        public PSExecution MyP4SyncLatest(string path) =>
            NewPSExecution($"p4 -c heron-workspace sync //{path}/...#head");
    }
}
