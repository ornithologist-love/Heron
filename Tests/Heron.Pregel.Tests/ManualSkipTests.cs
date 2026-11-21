namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md#手动跳过节点与 Stage")]
public sealed class ManualSkipTests
{
    [Fact]
    public async Task ManualSkip_SkipsChosenStageAndNode_AndKeepsBarriersAndLaterStages()
    {
        var steps = new Steps();
        var store = new InMemoryPregelCheckpointStore();
        var graph = Pregel.Define<int>("manual-skip")
            .Stage("build", "Build", stage => stage.Parallel(
                left => left.Then(steps.Left),
                right => right.Then(steps.Right)))
            .Stage("publish", "Publish", stage => stage.Then(steps.Upload).Then(steps.Verify))
            .Compile();
        var options = new PregelRunOptions
        {
            Engine = new PregelEngine(store),
            ManualSkip = new(new HashSet<NodeId> { Pregel.NodeId(steps.Upload) }, new HashSet<string> { "build" }, "只跑校验")
        };

        var result = await graph.Prepare(0, options).RunAsync();
        var history = await store.LoadHistoryAsync(result.RunId);

        result.Status.Should().Be(PregelRunStatus.Completed);
        steps.Ran.Should().BeEquivalentTo(["verify"], "only the nodes outside the manual selection run");
        foreach (var id in new[] { Pregel.NodeId(steps.Left), Pregel.NodeId(steps.Right), Pregel.NodeId(steps.Upload) })
            history.SelectMany(checkpoint => checkpoint.CommittedOutcomes)
                .Where(outcome => outcome.Invocation.NodeId == id)
                .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
                .Should().Contain(outcome => outcome.Reason == "只跑校验", $"{id} is recorded as a native skip");
    }

    [Fact]
    public async Task ManualSkip_RejectsBarrierControlNodesAndUnknownStages()
    {
        var steps = new Steps();
        var graph = Pregel.Define<int>("manual-skip-invalid")
            .Stage("build", "Build", stage => stage.Parallel(
                left => left.Then(steps.Left),
                right => right.Then(steps.Right)))
            .Compile();
        var join = graph.Compiled.Definition.Nodes.Keys.Single(id => id.Value.EndsWith(":join", StringComparison.Ordinal));

        foreach (var skip in new PregelManualSkip[]
        {
            new(new HashSet<NodeId> { join }, new HashSet<string>()),
            new(new HashSet<NodeId>(), new HashSet<string> { "missing" }),
        })
        {
            var act = async () => await graph.Prepare(0, options: new PregelRunOptions { ManualSkip = skip }).RunAsync();
            (await act.Should().ThrowAsync<PregelException>()).Which.Code.Should().Be("invalid_manual_skip");
        }
    }

    private sealed class Steps
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Ran { get; } = [];
        public void Left(int input, PregelNodeContext context, CancellationToken token) => Ran.Add("left");
        public void Right(int input, PregelNodeContext context, CancellationToken token) => Ran.Add("right");
        public void Upload(int input, PregelNodeContext context, CancellationToken token) => Ran.Add("upload");
        public void Verify(int input, PregelNodeContext context, CancellationToken token) => Ran.Add("verify");
    }
}
