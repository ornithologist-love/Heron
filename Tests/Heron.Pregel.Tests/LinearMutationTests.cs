namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class LinearMutationTests
{
    [Fact]
    public async Task LinearMutate_WritesChildResultsInPlannedOrder_IntoState()
    {
        var flow = new Flow();
        (await flow.Build().RunAsync(10)).Status.Should().Be(PregelRunStatus.Completed);
        flow.Seen.Should().Equal(24, 20, 22);
    }

    [Fact]
    public async Task LinearMutate_SkippedStage_LeavesIntoStateUnchanged()
    {
        var flow = new Flow();
        (await flow.Build(skip: true).RunAsync(10)).Status.Should().Be(PregelRunStatus.Completed);
        flow.Seen.Should().Equal(-1);
    }

    private sealed class Flow
    {
        private PregelVariable<int> _seed = null!;
        private PregelVariable<IReadOnlyList<int>> _results = null!;
        public int[] Seen = [];

        private static int Double(int value, PregelNodeContext context, CancellationToken token) => value * 2;
        private void Seed(int input, PregelNodeContext context, CancellationToken token) => context.Write(_seed, input);
        private void Observe(int input, PregelNodeContext context, CancellationToken token) => Seen = context.Read(_results).ToArray();

        private IReadOnlyList<PregelMutationItem<int>> Plan(int input, PregelNodeContext context, CancellationToken token)
        {
            var start = context.Read(_seed);
            return [new("c", start + 2), new("a", start), new("b", start + 1)];
        }

        public PregelGraph<int> Build(bool skip = false)
        {
            var child = Pregel.Define<int>("double").Start(Double).Compile();
            var flow = Pregel.Define<int>("parent");
            _seed = flow.State("seed", 0);
            _results = flow.State<IReadOnlyList<int>>("results", [-1]);
            return flow.Start(Seed)
                .Stage("expand", "展开", stage => stage.Mutate("items", child, Plan, _results), when: _ => !skip)
                .Then(Observe)
                .Compile();
        }
    }
}
