using System.Collections.Concurrent;

namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class CollectionBranchTests
{
    [Fact]
    public async Task LeadingWhen_GatesTheWholeFixedBranch_WhileLaterWhenStillGatesItsOwnNode()
    {
        const string reason = "not selected";
        var ran = new ConcurrentBag<string>();
        var joined = false;
        var store = new InMemoryPregelCheckpointStore();
        var graph = Pregel.Define<bool>("leading-when")
            .Stage("tools", "Tools", stage => stage.Parallel(
                node => node.When(selected => selected, reason)
                    .Then("detect-node", () => ran.Add("detect-node"))
                    .Then("verify-node", () => ran.Add("verify-node")),
                git => git.When(_ => true, "never")
                    .Then("detect-git", () => ran.Add("detect-git"))
                    .Then("install-git", () => ran.Add("install-git")).When(_ => false, "already installed"))
                .Then("joined", () => joined = true))
            .Compile();

        var result = await graph.Prepare(false, engine: new PregelEngine(store)).RunAsync();
        var outcomes = (await store.LoadHistoryAsync(result.RunId)).SelectMany(checkpoint => checkpoint.CommittedOutcomes).ToArray();

        result.Status.Should().Be(PregelRunStatus.Completed);
        ran.Should().BeEquivalentTo("detect-git");
        joined.Should().BeTrue("a gated branch must still arrive at the join");
        foreach (var id in new[] { "detect-node", "verify-node" })
            outcomes.Where(outcome => outcome.Invocation.NodeId.Value == id)
                .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
                .Should().Contain(outcome => outcome.Reason == reason);
    }

    [Fact]
    public void LeadingWhen_WithoutAnyNodeAfterIt_IsRejected()
    {
        var build = () => Pregel.Define<bool>("empty-branch")
            .Stage("tools", "Tools", stage => stage.Parallel(
                left => left.When(_ => true, "never"),
                right => right.Then("right", () => { })))
            .Compile();

        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(PregelMutationMode.Parallel)]
    [InlineData(PregelMutationMode.Sequential)]
    public async Task CollectionBranch_OfOneGraphCall_UsesThatGraph_AndExpandsOnlySelectedItems(PregelMutationMode mode)
    {
        var child = Pregel.Define<Unit>("report").Start(Report).Compile();
        var planned = Pregel.Define<Run>("daily").Start(Plan);
        Func<Run, IEnumerable<Unit>> epic = run => run.Units.Where(unit => unit.Tier == "epic");
        Func<Unit, string> key = unit => $"{unit.Scene}/{unit.Tier}";
        var graph = mode == PregelMutationMode.Parallel
            ? planned.Parallel("reports", epic, key, unit => unit.Graph("report", child)).Compile()
            : planned.ForEach("reports", epic, key, unit => unit.Graph("report", child)).Compile();

        var result = await graph.RunAsync(new Run([new("city", "epic"), new("city", "high"), new("forest", "epic")]));

        var rule = graph.Compiled.Definition.Mutations.Should().ContainSingle().Which;
        rule.Template.Fingerprint.Should().Be(child.Compiled.Definition.Fingerprint);
        rule.Mode.Should().Be(mode);
        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Equal("city/epic", "forest/epic");
    }

    [Fact]
    public async Task CollectionBranch_WithSteps_RunsEachItemAsItsOwnChild_AndLeadingWhenSkipsOnlyThatItem()
    {
        var ran = new ConcurrentBag<string>();
        var graph = Pregel.Define<Run>("inline").Start(Plan)
            .Parallel("units", run => run.Units, unit => unit.Scene, unit => unit
                .When(item => item.Tier != "low", "low tier")
                .Then("prepare", item => ran.Add("prepare:" + item.Scene))
                .Then("publish", item => ran.Add("publish:" + item.Scene)))
            .Then(Count)
            .Compile();

        var result = await graph.RunAsync(new Run([new("city", "epic"), new("forest", "low")]));

        result.Status.Should().Be(PregelRunStatus.Completed);
        ran.Should().BeEquivalentTo("prepare:city", "publish:city");
        graph.Read(result).Should().Be(2, "a collection branch without a result keeps the upstream result");
    }

    [Fact]
    public async Task CollectionBranch_WithAResult_CollectsOnlyExecutedItemsInOrder()
    {
        var store = new InMemoryPregelCheckpointStore();
        var child = Pregel.Define<Unit>("report").Start(Report).Compile();
        var graph = Pregel.Define<Run>("daily").Start(Plan)
            .ForEach("reports", run => run.Units, unit => unit.Scene, unit => unit
                .When(item => item.Tier == "epic", "low tier").Graph("report", child))
            .Compile();

        var input = new Run([new("city", "epic"), new("forest", "low"), new("river", "epic")]);
        var result = await graph.Prepare(input, engine: new PregelEngine(store)).RunAsync();
        var expansion = result.Checkpoint.Mutations.Should().ContainSingle().Which;
        var skipped = expansion.Instances.Single(item => item.Key == "forest");
        var outcomes = (await store.LoadHistoryAsync(result.RunId)).SelectMany(x => x.CommittedOutcomes);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Equal("city/epic", "river/epic");
        (await store.LoadLatestAsync(skipped.ChildRunId, CancellationToken.None)).Should().BeNull();
        outcomes.Where(x => x.Invocation.NodeId == skipped.NodeId).Select(x => x.Outcome)
            .OfType<PregelInvocationOutcome.Skipped>().Should().Contain(x => x.Reason == "low tier");
    }

    [Fact]
    public async Task SkipIf_CannotChangeAnAlreadyCompiledGraph()
    {
        var child = Pregel.Define<Unit>("report").Start(Report).Compile();
        var chain = Pregel.Define<Unit>("daily").Start(ReportInput).Graph("report", child);
        var graph = chain.Compile();

        var mutate = () => chain.SkipIf(_ => "too late");
        mutate.Should().Throw<InvalidOperationException>();
        graph.Compiled.Definition.Mutations.Should().ContainSingle().Which.HasResultSkipCondition.Should().BeFalse();
        graph.Read(await graph.RunAsync(new Unit("city", "epic"))).Should().Be("city/epic");
    }

    [Fact]
    public async Task SkipIf_OnTheGraphCall_MarksThatItemSkipped_AndKeepsItsResult()
    {
        var store = new InMemoryPregelCheckpointStore();
        var child = Pregel.Define<Unit>("report").Start(Report).Compile();
        Func<string, string?> skip = report => report.StartsWith("forest", StringComparison.Ordinal) ? "no data" : null;
        var graph = Pregel.Define<Run>("daily").Start(Plan)
            .Parallel("reports", run => run.Units, unit => unit.Scene, unit => unit.Graph("report", child).SkipIf(skip))
            .Compile();

        var result = await graph.Prepare(new Run([new("city", "epic"), new("forest", "epic")]), engine: new PregelEngine(store)).RunAsync();
        var outcomes = (await store.LoadHistoryAsync(result.RunId)).SelectMany(checkpoint => checkpoint.CommittedOutcomes);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Equal("city/epic", "forest/epic");
        outcomes.Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
            .Should().Contain(outcome => outcome.Reason == "no data");
    }

    [Fact]
    public void SkipIf_NotDirectlyAfterAGraphCall_IsRejected()
    {
        var build = () => Pregel.Define<Run>("misplaced").Start(Plan).SkipIf(_ => "never");

        build.Should().Throw<InvalidOperationException>();
    }

    private sealed record Run(Unit[] Units);

    private sealed record Unit(string Scene, string Tier);

    private static Run Plan(Run run, PregelNodeContext context, CancellationToken token) => run;

    private static Unit ReportInput(Unit unit, PregelNodeContext context, CancellationToken token) => unit;

    private static string Report(Unit unit, PregelNodeContext context, CancellationToken token) => $"{unit.Scene}/{unit.Tier}";

    private static Task<int> Count(Run run, PregelNodeContext context, CancellationToken token) => Task.FromResult(run.Units.Length);
}
