namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class LinearMutationTests
{
    [Fact]
    public async Task Map_WritesChildResultsInPlannedOrder_IntoCurrentValue()
    {
        var graph = Build(skip: false);
        var result = await graph.RunAsync(new(10, [-1]));
        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Results.Should().Equal(24, 20, 22);
    }

    [Fact]
    public async Task Map_SkippedStage_LeavesStateUnchanged()
    {
        var graph = Build(skip: true);
        var result = await graph.RunAsync(new(10, [-1]));
        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Results.Should().Equal(-1);
        result.Checkpoint.Mutations.Should().BeEmpty();
    }

    private static PregelGraph<FlowState, FlowState> Build(bool skip)
    {
        var child = Pregel.Define<int>("double").Then(Double).Compile();
        return Pregel.Define<FlowState>("parent")
            .Stage("expand", "展开", stage => stage
                .Map("items", Plan, value => value.ToString(), item => item.Graph("child", child))
                .Then(Store), when: _ => !skip)
            .Compile();
    }

    private static int Double(int value, PregelNodeContext context, CancellationToken token) => value * 2;
    private static IEnumerable<int> Plan(FlowState state) => [state.Seed + 2, state.Seed, state.Seed + 1];

    private static FlowState Store(PregelMapResult<FlowState, int> result, PregelNodeContext context,
        CancellationToken token) => result.State with { Results = result.Items };

    private sealed record FlowState(int Seed, IReadOnlyList<int> Results);
}
