namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md#手动继续与从节点重新运行")]
public sealed class DurableReexecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pregel-derived-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private PregelEngine Engine() => new(new FilePregelCheckpointStore(_root));

    [Fact]
    public async Task SuccessfulPeerAndItsWrites_SurviveDerivedSeedAndEngineRecreation()
    {
        var calls = 0;
        var fail = true;
        var b = new PregelGraphBuilder("durable-peer");
        var value = b.LastValueChannel("value", 0);
        var start = b.Node("start", (_, _) => ValueTask.CompletedTask);
        var peer = b.Node("peer", (c, _) => { calls++; c.Write(value, 7); return ValueTask.CompletedTask; });
        var retry = b.Node("retry", (_, _) => fail ? throw new IOException("synthetic") : ValueTask.CompletedTask);
        var join = b.Node("join", (c, _) => { c.Read(value).Should().Be(7); return ValueTask.CompletedTask; });
        var graph = b.SetEntry(start).AddEdge(start, peer).AddEdge(start, retry).AddEdge(peer, join).AddEdge(retry, join).Compile();
        var source = await Engine().StartAsync(graph);
        source.Status.Should().Be(PregelRunStatus.Failed);
        var original = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllText);
        var derived = new PregelRunId("derived-peer");
        var preview = await Engine().PreviewReexecutionAsync(graph, source.RunId, new(PregelReexecutionMode.Continue, []));
        preview.ReplayedNodes.Should().Contain(peer.Id);
        await Engine().CreateReexecutionAsync(graph, source.RunId, derived, new(PregelReexecutionMode.Continue, []));
        fail = false;
        (await Engine().ResumeAsync(graph, derived)).Status.Should().Be(PregelRunStatus.Completed);
        calls.Should().Be(1, "the successful peer is durably seeded before the derived run starts");
        foreach (var file in original) File.ReadAllText(file.Key).Should().Be(file.Value, "the source run is immutable");
    }

    [Fact]
    public async Task FromNode_RestoresPreNodeState_AfterStoreRecreation()
    {
        var prefixCalls = 0;
        var observed = new List<int>();
        var b = new PregelGraphBuilder("durable-from-node");
        var value = b.LastValueChannel("value", 0);
        var first = b.Node("first", (c, _) => { prefixCalls++; c.Write(value, 7); return ValueTask.CompletedTask; });
        var middle = b.Node("middle", (c, _) => { observed.Add(c.Read(value)); c.Write(value, c.Read(value) + 10); return ValueTask.CompletedTask; });
        var last = b.Node("last", (c, _) => { c.Read(value).Should().Be(17); return ValueTask.CompletedTask; });
        var graph = b.SetEntry(first).AddEdge(first, middle).AddEdge(middle, last).Compile();
        var source = await Engine().StartAsync(graph);
        source.Status.Should().Be(PregelRunStatus.Completed);
        var derived = new PregelRunId("from-middle");
        await Engine().CreateReexecutionAsync(graph, source.RunId, derived, new(PregelReexecutionMode.FromNode, [middle.Id]));
        (await Engine().ResumeAsync(graph, derived)).Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Equal(new[] { 7, 7 }, "reexecution uses the selected barrier's state, not final state");
        prefixCalls.Should().Be(1);
    }

    [Fact]
    public async Task CancelledBody_ContinuesInNewRun_WithoutRepeatingCommittedPrefix()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var cancelBody = true;
        var b = new PregelGraphBuilder("cancelled-derived");
        var first = b.Node("first", (_, _) => { calls++; return ValueTask.CompletedTask; });
        var second = b.Node("second", (_, _) => { if (cancelBody) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } return ValueTask.CompletedTask; });
        var graph = b.SetEntry(first).AddEdge(first, second).Compile();
        var source = await Engine().StartAsync(graph, cancellation.Token);
        source.Status.Should().Be(PregelRunStatus.Cancelled);
        var derived = new PregelRunId("derived-cancelled");
        await Engine().CreateReexecutionAsync(graph, source.RunId, derived, new(PregelReexecutionMode.Continue, []));
        cancelBody = false;
        (await Engine().ResumeAsync(graph, derived)).Status.Should().Be(PregelRunStatus.Completed);
        calls.Should().Be(1);
        (await Engine().GetStateAsync(source.RunId))!.Status.Should().Be(PregelRunStatus.Cancelled);
    }

    [Theory]
    [InlineData(PregelMutationMode.Sequential)]
    [InlineData(PregelMutationMode.Parallel)]
    public async Task NativeMutationContinue_RestoresChildProgress_AndPreservesCompletedInstances(PregelMutationMode mode)
    {
        var flow = new MutationFlow();
        var child = Pregel.Define<int>("child").Start(flow.Prepare).Then(flow.Finish).Compile();
        var graph = Pregel.Define<int>("parent").Start(MutationFlow.Input)
            .Mutate("items", child, MutationFlow.Select, mode).Compile();
        var source = await graph.Prepare(0, engine: Engine()).RunAsync();
        source.Status.Should().Be(PregelRunStatus.Failed);
        var derived = new PregelRunId("derived-mutation");
        await Engine().CreateReexecutionAsync(graph.Compiled, source.RunId, derived, new(PregelReexecutionMode.Continue, []));
        flow.Fail = false;
        var completed = await Engine().ResumeAsync(graph.Compiled, derived);
        completed.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(completed).Should().Equal(1, 2);
        flow.Prepared[1].Should().Be(1);
        flow.Prepared[2].Should().Be(1, "the failed child continues after its own committed prefix");
        flow.Finished[1].Should().Be(1, "the completed child is reused");
        flow.Finished[2].Should().Be(2);
        (await Engine().GetStateAsync(source.RunId))!.Status.Should().Be(PregelRunStatus.Failed);
    }

    private sealed class MutationFlow
    {
        public bool Fail = true;
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Prepared = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Finished = new();
        public static int Input(int value, PregelNodeContext context, CancellationToken token) => value;
        public static IReadOnlyList<PregelMutationItem<int>> Select(int _) => [new("one", 1), new("two", 2)];
        public int Prepare(int value, PregelNodeContext context, CancellationToken token) { Prepared.AddOrUpdate(value, 1, (_, n) => n + 1); return value; }
        public int Finish(int value, PregelNodeContext context, CancellationToken token) { Finished.AddOrUpdate(value, 1, (_, n) => n + 1); if (Fail && value == 2) throw new IOException("synthetic child failure"); return value; }
    }
}
