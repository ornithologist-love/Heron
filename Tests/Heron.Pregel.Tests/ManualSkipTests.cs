namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md#手动跳过节点与 Stage")]
public sealed class ManualSkipTests
{
    [Fact]
    public async Task ManualSkip_SkipsChosenStageAndNode_AndKeepsBarriersAndLaterStages()
    {
        var ran = new System.Collections.Concurrent.ConcurrentBag<string>();
        var store = new InMemoryPregelCheckpointStore();
        var graph = Pregel.Define<int>("manual-skip")
            .Stage("build", "Build", stage => stage.Parallel(
                left => left.Then("left", () => ran.Add("left")),
                right => right.Then("right", () => ran.Add("right"))))
            .Stage("publish", "Publish", stage => stage
                .Then("upload", () => ran.Add("upload"))
                .Then("verify", () => ran.Add("verify")))
            .Compile();
        var options = new PregelRunOptions
        {
            ManualSkip = new(new HashSet<NodeId> { new("upload") }, new HashSet<string> { "build" }, "只跑校验")
        };

        var result = await graph.Prepare(0, engine: new PregelEngine(store), options: options).RunAsync();
        var history = await store.LoadHistoryAsync(result.RunId);

        result.Status.Should().Be(PregelRunStatus.Completed);
        ran.Should().BeEquivalentTo(["verify"], "only the nodes outside the manual selection run");
        foreach (var id in new[] { "left", "right", "upload" })
            history.SelectMany(checkpoint => checkpoint.CommittedOutcomes)
                .Where(outcome => outcome.Invocation.NodeId.Value == id)
                .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
                .Should().Contain(outcome => outcome.Reason == "只跑校验", $"{id} is recorded as a native skip");
    }

    [Fact]
    public async Task ManualSkip_RejectsBarrierControlNodesAndUnknownStages()
    {
        var graph = Pregel.Define<int>("manual-skip-invalid")
            .Stage("build", "Build", stage => stage.Parallel(
                left => left.Then("left", () => { }),
                right => right.Then("right", () => { })))
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
}
