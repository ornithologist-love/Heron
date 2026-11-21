namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md#手动继续与从节点重新运行")]
public sealed class ReexecutionTests
{
    private sealed class Chain
    {
        public readonly Dictionary<string, int> Executions = new(StringComparer.Ordinal);
        public bool FailMiddle = true;
        public CompiledPregelGraph Graph { get; }

        public Chain()
        {
            var builder = new PregelGraphBuilder("reexecution-chain");
            var first = builder.Node("first", (_, _) => Count("first"));
            var middle = builder.Node("middle", (_, _) =>
            {
                Count("middle");
                return FailMiddle ? throw new InvalidOperationException("middle failed") : ValueTask.CompletedTask;
            });
            var last = builder.Node("last", (_, _) => Count("last"));
            Graph = builder.SetEntry(first).AddEdge(first, middle).AddEdge(middle, last).Compile();
        }

        private ValueTask Count(string node)
        {
            Executions[node] = Executions.GetValueOrDefault(node) + 1;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Events : IPregelRunObserver
    {
        public readonly List<PregelRunEvent> Items = [];
        public void OnEvent(PregelRunEvent value) { lock (Items) Items.Add(value); }
        public string GetRunDirectory(PregelRunId runId) => Path.Combine(Path.GetTempPath(), "heron-reexecution", runId.Value);
    }

    [Fact]
    public async Task Continue_LeavesSourceUntouched_AndDoesNotRepeatCommittedNodes()
    {
        var chain = new Chain();
        var engine = new PregelEngine();
        var failed = await engine.StartAsync(chain.Graph);
        failed.Status.Should().Be(PregelRunStatus.Failed);
        var sourceBefore = await engine.GetStateAsync(failed.RunId);

        chain.FailMiddle = false;
        var derived = new PregelRunId("derived");
        await engine.CreateReexecutionAsync(chain.Graph, failed.RunId, derived, new(PregelReexecutionMode.Continue, []));
        var result = await engine.ResumeAsync(chain.Graph, derived);

        result.Status.Should().Be(PregelRunStatus.Completed);
        chain.Executions["first"].Should().Be(1, "a node committed before the restored barrier is inherited, not executed");
        chain.Executions["last"].Should().Be(1);
        var sourceAfter = await engine.GetStateAsync(failed.RunId);
        sourceAfter!.CheckpointId.Should().Be(sourceBefore!.CheckpointId, "the source run keeps its terminal state");
        sourceAfter.Status.Should().Be(PregelRunStatus.Failed);
    }

    [Fact]
    public async Task FromNode_ExecutesSelectedNodeAndLaterBarriers_Only()
    {
        var chain = new Chain { FailMiddle = false };
        var engine = new PregelEngine();
        var completed = await engine.StartAsync(chain.Graph);
        completed.Status.Should().Be(PregelRunStatus.Completed);
        chain.Executions.Clear();

        var derived = new PregelRunId("from-middle");
        await engine.CreateReexecutionAsync(chain.Graph, completed.RunId, derived,
            new(PregelReexecutionMode.FromNode, [new NodeId("middle")]));
        var result = await engine.ResumeAsync(chain.Graph, derived);

        result.Status.Should().Be(PregelRunStatus.Completed);
        chain.Executions.Should().NotContainKey("first");
        chain.Executions["middle"].Should().Be(1);
        chain.Executions["last"].Should().Be(1);
    }

    [Fact]
    public async Task Preview_RejectsLikeCreate_WithoutCreatingAnyRun()
    {
        var chain = new Chain { FailMiddle = false };
        var store = new InMemoryPregelCheckpointStore();
        var engine = new PregelEngine(store);
        var completed = await engine.StartAsync(chain.Graph);

        var continueCompleted = () => engine.PreviewReexecutionAsync(chain.Graph, completed.RunId,
            new(PregelReexecutionMode.Continue, [])).AsTask();
        (await continueCompleted.Should().ThrowAsync<PregelException>()).Which.Code.Should().Be("reexecution_not_stopped");
        var create = () => engine.CreateReexecutionAsync(chain.Graph, completed.RunId, new PregelRunId("never"),
            new(PregelReexecutionMode.Continue, [])).AsTask();
        (await create.Should().ThrowAsync<PregelException>()).Which.Code.Should().Be("reexecution_not_stopped");

        var preview = await engine.PreviewReexecutionAsync(chain.Graph, completed.RunId,
            new(PregelReexecutionMode.FromNode, [new NodeId("last")]));
        preview.InheritedNodes.Select(x => x.NodeId.Value).Should().Contain(["first", "middle"]);
        (await store.LoadLatestAsync(new PregelRunId("never"))).Should().BeNull();
        (await store.LoadLatestAsync(new PregelRunId("reexecution-preview"))).Should().BeNull("a preview never persists a run");
    }

    private static void Prepare(PregelUnit _, PregelNodeContext context, CancellationToken token) { }
    private static Task Fail(PregelUnit _, PregelNodeContext context, CancellationToken token) => throw new InvalidOperationException("boom");
    private static void Cleanup(PregelOutcome<PregelUnit, PregelUnit> _, PregelNodeContext context, CancellationToken token) { }

    [Fact]
    public async Task GraphWithFinally_RefusesMidRunRestore()
    {
        var graph = Pregel.Define<PregelUnit>("reexecution-finally")
            .Then(Prepare)
            .Then(Fail)
            .Finally(stage => stage.Then(Cleanup))
            .Compile();
        var engine = new PregelEngine();
        var failed = await graph.Prepare(PregelUnit.Value, new() { Engine = engine }).RunAsync();
        failed.Status.Should().Be(PregelRunStatus.Failed);

        var preview = () => engine.PreviewReexecutionAsync(graph.Compiled, failed.RunId, new(PregelReexecutionMode.Continue, [])).AsTask();

        (await preview.Should().ThrowAsync<PregelException>()).Which.Code.Should().Be("reexecution_resources_released");
    }

    private sealed class CrashOnFirstBarrier(IPregelCheckpointStore inner) : IPregelCheckpointStore
    {
        private bool _crashed;
        public ValueTask CreateRunAsync(PregelCheckpoint initial, CancellationToken token = default) => inner.CreateRunAsync(initial, token);
        public ValueTask<PregelCheckpoint?> LoadLatestAsync(PregelRunId runId, CancellationToken token = default) => inner.LoadLatestAsync(runId, token);
        public ValueTask PutOutcomeAsync(PregelRunId runId, PregelPendingOutcome outcome, CancellationToken token = default) =>
            inner.PutOutcomeAsync(runId, outcome, token);
        public ValueTask<IReadOnlyList<PregelPendingOutcome>> LoadOutcomesAsync(PregelRunId runId, PregelCheckpointId baseId,
            CancellationToken token = default) => inner.LoadOutcomesAsync(runId, baseId, token);
        public ValueTask CommitBarrierAsync(PregelRunId runId, PregelBarrierCommit commit, CancellationToken token = default)
        {
            // Lifecycle commits consume nothing; crash on the first barrier that consumes a node outcome.
            if (_crashed || commit.ConsumedOutcomeIds.Count == 0) return inner.CommitBarrierAsync(runId, commit, token);
            _crashed = true;
            throw new IOException("simulated crash before the barrier commit");
        }
    }

    [Fact]
    public async Task OrdinaryRecovery_DoesNotReportReuse()
    {
        var chain = new Chain { FailMiddle = false };
        var engine = new PregelEngine(new CrashOnFirstBarrier(new InMemoryPregelCheckpointStore()));
        var runId = new PregelRunId("ordinary");
        var crash = () => engine.StartAsync(chain.Graph, runId).AsTask();
        await crash.Should().ThrowAsync<Exception>();
        (await engine.GetStateAsync(runId))!.Status.Should().Be(PregelRunStatus.Running, "the crash left a durable outcome to replay");

        var events = new Events();
        var result = await engine.ResumeAsync(chain.Graph, runId, new PregelRunOptions(Observer: events));

        result.Status.Should().Be(PregelRunStatus.Completed);
        chain.Executions["first"].Should().Be(1, "the durable outcome is replayed");
        events.Items.Should().NotContain(x => x.Kind == "node.reused",
            "only seeds copied from a source run are reuse; a run's own durable outcomes are ordinary replay");
    }
}
