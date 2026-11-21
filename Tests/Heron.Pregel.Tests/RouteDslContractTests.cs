namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class RouteDslContractTests
{
    [Fact]
    public async Task WhenFalse_SkipsBodyWithoutWriting_AndContinuesWithTheSkipReason()
    {
        const int initialValue = 17;
        const string skipReason = "optional step disabled";
        var nodes = new ConditionalNodes();
        var graph = Pregel.Define<int>("when-skip-contract").Stage("optional", "Optional", stage => stage
            .Then(nodes.Write).When(_ => false, skipReason)
            .Then(nodes.Observe)).Compile();
        var store = new InMemoryPregelCheckpointStore();

        var result = await graph.Prepare(initialValue, new() { Engine = new PregelEngine(store) }).RunAsync();
        var history = await store.LoadHistoryAsync(result.RunId);

        result.Status.Should().Be(PregelRunStatus.Completed);
        nodes.BodyRan.Should().BeFalse();
        nodes.Observed.Should().Be(initialValue, "a skipped node must not publish output and must still trigger its successor");
        history.SelectMany(checkpoint => checkpoint.CommittedOutcomes)
            .Where(outcome => outcome.Invocation.NodeId == Pregel.NodeId(nodes.Write))
            .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
            .Should().Contain(outcome => outcome.Reason == skipReason, "skipping must retain its declared reason");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnIf_CommitsTheCurrentResult_AndOnlyStopsWhenItsPredicateMatches(bool stopEarly)
    {
        var input = stopEarly ? 11 : -11;
        var nodes = new ResultNodes();
        var graph = Pregel.Define<int>("fast-return-contract")
            .Stage("check", "Check", stage => stage.Then(nodes.Identity).ReturnIf(value => value > 0))
            .Stage("next", "Next", stage => stage.Then(nodes.Double))
            .Compile();
        var run = graph.Prepare(input);

        var result = await run.RunAsync();

        result.Status.Should().Be(PregelRunStatus.Completed);
        nodes.NextRan.Should().Be(!stopEarly, "ReturnIf must suppress successors only when its predicate matches");
        run.Read().Should().Be(stopEarly ? input : input + input, "the result must be committed even when the flow ends early");
    }

    [Fact]
    public async Task Route_RejectsAnUndeclaredTarget_EvenWhenItsOutgoingEdgeExists()
    {
        var executed = new List<NodeId>();
        var builder = new PregelGraphBuilder("undeclared-route-contract");
        var route = builder.Node("route", (_, _) => ValueTask.CompletedTask);
        var declared = builder.Node("declared", (context, _) =>
        {
            executed.Add(context.NodeId);
            return ValueTask.CompletedTask;
        });
        var undeclared = builder.Node("undeclared", (context, _) =>
        {
            executed.Add(context.NodeId);
            return ValueTask.CompletedTask;
        });
        builder.Route(route, PregelRoutePhase.AfterNode,
            _ => PregelRoute.To(undeclared), PregelRoute.To(declared));
        var graph = builder.SetEntry(route).AddEdge(route, declared).AddEdge(route, undeclared).Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Failed);
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("undeclared_route");
        executed.Should().BeEmpty("an invalid route must fail before either successor executes");
    }

    private sealed class ConditionalNodes
    {
        public bool BodyRan { get; private set; }
        public int? Observed { get; private set; }

        public int Write(int value, PregelNodeContext context, CancellationToken token)
        {
            BodyRan = true;
            return value + 1;
        }

        public void Observe(int value, PregelNodeContext context, CancellationToken token) => Observed = value;
    }

    private sealed class ResultNodes
    {
        public bool NextRan { get; private set; }

        public int Identity(int input, PregelNodeContext context, CancellationToken token) => input;

        public int Double(int value, PregelNodeContext context, CancellationToken token)
        {
            NextRan = true;
            return value + value;
        }
    }
}
