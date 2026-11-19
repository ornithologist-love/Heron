namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class SubgraphConnectionTests
{
    [Fact]
    public async Task Graph_ForwardsCurrentResult_AndMatchesLegacySubgraphCalls()
    {
        var leaf = Pregel.Define<int>("leaf").Start(Double).Compile();
        var child = Pregel.Define<int>("child")
            .Stage("inner", "Inner", stage => stage.Graph("leaf", leaf))
            .Compile();
        var direct = Pregel.Define<string>("parent").Start(Length).Graph("call", child).Compile();
        var legacy = Pregel.Define<string>("parent").Start(Length).Subgraph("call", child).Compile();
        var original = Pregel.Define<string>("parent").Start(Length).Subgraph("call", child, value => value).Compile();

        direct.Compiled.Definition.Fingerprint.Should().Be(original.Compiled.Definition.Fingerprint);
        direct.Compiled.Definition.Fingerprint.Should().Be(legacy.Compiled.Definition.Fingerprint);
        var run = await direct.RunAsync("abc");
        run.Status.Should().Be(PregelRunStatus.Completed);
        direct.Read(run).Should().Be(6);
    }

    [Fact]
    public async Task Graph_WithoutOutput_PreservesParentInput()
    {
        var observation = new Observation();
        var child = Pregel.Define<int>("child").Start(observation.Observe).Compile();
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

    private static int Double(int value, PregelNodeContext context, CancellationToken token) => value * 2;

    private sealed class Observation
    {
        public int Seen { get; private set; }

        public void Observe(int input, PregelNodeContext context, CancellationToken token) => Seen = input;
    }
}
