namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class LinearMutationTests
{
    [Fact]
    public async Task ForEach_WritesChildResultsInPlannedOrder_IntoState()
    {
        var flow = new Flow();
        (await flow.Build().RunAsync(10)).Status.Should().Be(PregelRunStatus.Completed);
        flow.Seen.Should().Equal(24, 20, 22);
    }

    [Fact]
    public async Task ForEach_SkippedStage_LeavesStateUnchanged()
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
        private Task<int> Observe(int input, PregelNodeContext context, CancellationToken token)
        {
            Seen = context.Read(_results).ToArray();
            return Task.FromResult(input);
        }

        private IReadOnlyList<int> Plan(int input, PregelNodeContext context, CancellationToken token)
        {
            var start = context.Read(_seed);
            return [start + 2, start, start + 1];
        }

        private Task<int> Store(IReadOnlyList<int> values, PregelNodeContext context, CancellationToken token)
        {
            context.Write(_results, values);
            return Task.FromResult(values.Count);
        }

        public PregelGraph<int, int> Build(bool skip = false)
        {
            var child = Pregel.Define<int>("double").Start(Double).Compile();
            var flow = Pregel.Define<int>("parent");
            _seed = flow.State("seed", 0);
            _results = flow.State<IReadOnlyList<int>>("results", [-1]);
            return flow.Start(Seed)
                .Stage("expand", "展开", stage => stage.Then(Plan)
                    .ForEach("items", values => values, value => value.ToString(), item => item.Graph("child", child))
                    .Then(Store), when: _ => !skip)
                .Then(Observe)
                .Compile();
        }
    }
}
