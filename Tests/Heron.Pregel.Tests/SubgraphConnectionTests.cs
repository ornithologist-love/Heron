namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class SubgraphConnectionTests
{
    [Fact]
    public async Task Graph_ForwardsCurrentResult_ToTheNestedGraph()
    {
        var leaf = Pregel.Define<int>("leaf").Then(Double).Compile();
        var child = Pregel.Define<int>("child")
            .Stage("inner", "Inner", stage => stage.Graph("leaf", leaf))
            .Compile();
        var direct = Pregel.Define<string>("parent").Then(Length).Graph("call", child).Compile();
        var run = await direct.RunAsync("abc");
        run.Status.Should().Be(PregelRunStatus.Completed);
        direct.Read(run).Should().Be(6);
    }

    [Fact]
    public async Task Graph_WithEffectOnlyChild_ForwardsTheUnchangedValue()
    {
        var observation = new Observation();
        var child = Pregel.Define<int>("child").Then(observation.Observe).Compile();
        var parent = Pregel.Define<int>("parent")
            .Stage("observe", "Observe", stage => stage.Graph("call", child))
            .Then(Double)
            .Compile();

        var run = await parent.RunAsync(3);

        run.Status.Should().Be(PregelRunStatus.Completed);
        observation.Seen.Should().Be(3);
        parent.Read(run).Should().Be(6);
    }

    private static int Length(string value, PregelNodeContext context, CancellationToken token) => value.Length;

    private static Task<int> Double(int value, PregelNodeContext context, CancellationToken token) => Task.FromResult(value * 2);

    private sealed class Observation
    {
        public int Seen { get; private set; }

        public void Observe(int input, PregelNodeContext context, CancellationToken token) => Seen = input;
    }
}
