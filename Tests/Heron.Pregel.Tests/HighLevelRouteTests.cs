namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class HighLevelRouteTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("c")]
    public async Task NamedBranches_RunOnlyTheSelectedBodies_AndJoinAfterTheWholeBranch(string key)
    {
        var executed = new List<string>();
        var completed = false;
        var joinSawCompletion = false;
        var graph = Pregel.Define<string>("named-route")
            .Stage("work", "Work", stage => stage
                .Route("select", input => PregelRoute.Branch(input),
                    ("a", branch => branch.Then("a-first", () => executed.Add("a"))
                        .Then("a-last", () => completed = true)),
                    ("b", branch => branch.Then("b", () => { executed.Add("b"); completed = true; })),
                    ("c", branch => branch.Then("c", () => { executed.Add("c"); completed = true; })))
                .Then("joined", () => joinSawCompletion = completed))
            .Compile();

        var result = await graph.RunAsync(key);

        result.Status.Should().Be(PregelRunStatus.Completed);
        executed.Should().Equal(key);
        joinSawCompletion.Should().BeTrue("shorter skipped branches must not trigger the join ahead of the selected branch");
    }

    [Fact]
    public async Task Skip_PreservesReasonsAcrossNestedRoutesAndParallel_WithoutEvaluatingInnerSelectors()
    {
        const string reason = "not selected for this run";
        var innerEvaluated = false;
        var joined = false;
        var store = new InMemoryPregelCheckpointStore();
        var graph = Pregel.Define<int>("nested-route-skip")
            .Stage("work", "Work", stage => stage.Parallel(
                left => left.Route("outer", _ => PregelRoute.Skip(reason),
                    ("run", body => body.Parallel(
                        first => first.Route("inner", _ => { innerEvaluated = true; return PregelRoute.End; },
                            ("run", branch => branch.Then("inner-body", () => throw new InvalidOperationException()))),
                        second => second.Then("other-body", () => throw new InvalidOperationException())))),
                right => right.Then("peer", () => { }))
                .Then("join", () => joined = true))
            .Compile();

        var result = await graph.Prepare(0, engine: new PregelEngine(store)).RunAsync();
        var history = await store.LoadHistoryAsync(result.RunId);

        result.Status.Should().Be(PregelRunStatus.Completed);
        innerEvaluated.Should().BeFalse();
        joined.Should().BeTrue("skipped branches must still satisfy their enclosing barriers");
        foreach (var id in new[] { "inner-body", "other-body" })
            history.SelectMany(checkpoint => checkpoint.CommittedOutcomes)
                .Where(outcome => outcome.Invocation.NodeId.Value == id)
                .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
                .Should().Contain(outcome => outcome.Reason == reason);
    }

    [Fact]
    public async Task DisabledStage_DoesNotEvaluateItsRoute_AndStillContinues()
    {
        var nextRan = false;
        var graph = Pregel.Define<int>("disabled-route-stage")
            .Stage("disabled", "Disabled", stage => stage.Route("select",
                _ => throw new InvalidOperationException("a skipped stage must not evaluate selectors"),
                ("run", branch => branch.Then("body", () => throw new InvalidOperationException()))), when: _ => false)
            .Stage("next", "Next", stage => stage.Then("next", () => nextRan = true))
            .Compile();

        var result = await graph.RunAsync(0);

        result.Status.Should().Be(PregelRunStatus.Completed);
        nextRan.Should().BeTrue();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-7)]
    public async Task ResultRoute_SelectsFromTheFreshResult_AndEndPreservesIt(int input)
    {
        var nodes = new ResultNodes();
        var branchRan = false;
        var graph = Pregel.Define<int>("fresh-result-route")
            .Stage("check", "Check", stage => stage.Then(nodes.Identity)
                .Route("result", result => result > 0 ? PregelRoute.End : PregelRoute.Branch("continue"),
                    ("continue", branch => branch.Then("body", () => branchRan = true))))
            .Stage("next", "Next", stage => stage.Then(nodes.Double))
            .Compile();
        var run = graph.Prepare(input);

        var result = await run.RunAsync();

        result.Status.Should().Be(PregelRunStatus.Completed);
        branchRan.Should().Be(input <= 0);
        run.Read().Should().Be(input > 0 ? input : input + input);
    }

    [Fact]
    public async Task UndeclaredBranch_FailsBeforeExecutingABranch()
    {
        var bodyRan = false;
        var graph = Pregel.Define<int>("unknown-branch")
            .Stage("work", "Work", stage => stage.Route("select", _ => PregelRoute.Branch("unknown"),
                ("known", branch => branch.Then("body", () => bodyRan = true))))
            .Compile();

        var result = await graph.RunAsync(0);

        result.Status.Should().Be(PregelRunStatus.Failed);
        result.Error!.Code.Should().Be("undeclared_route");
        bodyRan.Should().BeFalse();
    }

    [Fact]
    public async Task Resume_ReusesTheCommittedSelection()
    {
        var selections = 0;
        var bodyRan = false;
        var graph = Pregel.Define<int>("resume-route")
            .Stage("work", "Work", stage => stage.Route("select",
                _ => { selections++; return PregelRoute.Branch("run"); },
                ("run", branch => branch.Then("body", () => bodyRan = true))))
            .Compile();
        var run = graph.Prepare(0, options: new PregelRunOptions(InterruptBefore: PregelInterruptSelector.Nodes(new NodeId("body"))));

        var interrupted = await run.RunAsync();
        var completed = await run.RunAsync();

        interrupted.Status.Should().Be(PregelRunStatus.Interrupted);
        completed.Status.Should().Be(PregelRunStatus.Completed);
        selections.Should().Be(1, "resuming after the routing barrier must retain its chosen branch");
        bodyRan.Should().BeTrue();
    }

    private sealed class ResultNodes
    {
        public int Identity(int input, PregelNodeContext context, CancellationToken token) => input;
        public int Double(int input, PregelNodeContext context, CancellationToken token) => input + input;
    }
}
