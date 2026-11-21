namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class FlowCompositionTests
{
    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 26)]
    public async Task ConditionalStage_AfterRoundTrip_PreservesTheIncomingValueWhenSkipped(bool enabled, int expected)
    {
        var steps = new RoundTrip();
        var graph = Pregel.Define<int>("conditional-round-trip")
            .Then(steps.Seed)
            .Stage("convert", "Convert", stage => stage.Then(steps.Format).Then(steps.Parse),
                when: value => enabled && value == 13)
            .Then(steps.Observe)
            .Compile();

        var result = await graph.RunAsync(3);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be(expected);
        steps.Seen.Should().Equal(expected);
        steps.Conversions.Should().Be(enabled ? 2 : 0);
    }

    [Fact]
    public void ConditionalStage_RejectsAChangedExitType()
    {
        var steps = new RoundTrip();
        var build = () => Pregel.Define<int>("conditional-type-change")
            .Stage("convert", "Convert", stage => stage.Then(steps.Format), when: _ => false);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Parallel_BranchesSeeIndependentValuesAndJoinWithTheOriginal()
    {
        var steps = new Branches();
        var graph = Pregel.Define<int>("isolated-parallel")
            .Stage("branches", "Branches", stage => stage.Parallel(
                left => left.Then(steps.AddOne).Then(steps.ObserveLeft),
                right => right.Then(steps.AddTwo).Then(steps.ObserveRight),
                gated => gated.When(_ => false, "disabled").Then(steps.Gated)))
            .Compile();

        var result = await graph.RunAsync(5);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be(5);
        steps.Left.Should().Be(6);
        steps.Right.Should().Be(7);
        steps.GatedRan.Should().BeFalse();
    }

    [Theory]
    [InlineData(PregelMutationMode.Parallel, false)]
    [InlineData(PregelMutationMode.Sequential, false)]
    [InlineData(PregelMutationMode.Sequential, true)]
    public async Task Map_PreservesStateAndCollectsResultsInSelectionOrder(PregelMutationMode mode, bool empty)
    {
        var graph = Pregel.Define<Batch>("map-state")
            .Map("items", state => state.Items, item => item.ToString(), item => item.Then(Format), mode)
            .Compile();
        var input = new Batch("keep", empty ? [] : [3, 1, 2]);

        var result = await graph.RunAsync(input);

        result.Status.Should().Be(PregelRunStatus.Completed);
        var output = graph.Read(result);
        output.State.Label.Should().Be("keep");
        output.State.Items.Should().Equal(input.Items);
        output.Items.Should().Equal(empty ? [] : ["3", "1", "2"]);
        result.Checkpoint.Mutations.Single().Instances.Select(item => item.Key)
            .Should().Equal(empty ? [] : ["3", "1", "2"]);
    }

    [Fact]
    public async Task ForEach_ExecutesInOrderAndKeepsTheIncomingState()
    {
        var steps = new RoundTrip();
        var graph = Pregel.Define<Batch>("for-each-state")
            .ForEach("items", state => state.Items, item => item.ToString(), item => item.Then(steps.Seed).Then(steps.Observe))
            .Compile();

        var result = await graph.RunAsync(new("keep", [3, 1]));

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Items.Should().Equal(3, 1);
        steps.Seen.Should().Equal(13, 11);
    }

    [Fact]
    public async Task Graph_SkipIf_KeepsTheResultAvailableAndSuppliesTheReason()
    {
        var child = Pregel.Define<int>("child").Then(Format).Compile();
        var graph = Pregel.Define<int>("caller").Graph("child", child).SkipIf(_ => "empty report").Compile();
        var store = new InMemoryPregelCheckpointStore();

        var result = await graph.Prepare(3, new() { Engine = new PregelEngine(store) }).RunAsync();

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be("3");
        var history = await store.LoadHistoryAsync(result.RunId);
        history.SelectMany(checkpoint => checkpoint.CommittedOutcomes)
            .Select(outcome => outcome.Outcome).OfType<PregelInvocationOutcome.Skipped>()
            .Should().Contain(outcome => outcome.Reason == "empty report");
    }

    [Fact]
    public async Task Map_UsesTheChildGraphDirectlyAndOmitsItemsGatedBeforeExecution()
    {
        var child = Pregel.Define<int>("item-child").Then(Format).Compile();
        var graph = Pregel.Define<int[]>("items-caller")
            .Map("items", items => items, item => item.ToString(), item => item
                .When(value => value > 1, "too small")
                .Graph("format", child))
            .Compile();

        var result = await graph.RunAsync([3, 1, 2]);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Items.Should().Equal("3", "2");
        graph.Compiled.Definition.Mutations.Single().Template.Fingerprint.Should().Be(child.Compiled.Definition.Fingerprint);
    }

    [Fact]
    public void SkipIf_RequiresTheImmediatelyPrecedingGraphCall()
    {
        var flow = Pregel.Define<int>("misplaced-skip").Then(Format);
        var build = () => flow.SkipIf(_ => "irrelevant");

        build.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false, PregelRunStatus.Completed)]
    [InlineData(true, PregelRunStatus.Failed)]
    public async Task Finally_DistinguishesCommittedNullFromAnUnsetValue(bool fail, PregelRunStatus expected)
    {
        var steps = new Cleanup(fail);
        var graph = Pregel.Define<int>("cleanup-outcome")
            .Then(steps.SeedOrFail)
            .Then(ToNull)
            .Finally(cleanup => cleanup.Then(steps.Capture))
            .Compile();

        var result = await graph.RunAsync(4);

        result.Status.Should().Be(expected);
        steps.Outcome.Should().NotBeNull();
        var outcome = steps.Outcome!;
        outcome.Input.Should().Be(4);
        outcome.Status.Should().Be(expected);
        outcome.HasValue.Should().Be(!fail);
        outcome.Value.Should().BeNull();
        (outcome.Error is not null).Should().Be(fail);
    }

    private static string Format(int value, PregelNodeContext context, CancellationToken token) => value.ToString();
    private static string? ToNull(int value, PregelNodeContext context, CancellationToken token) => null;

    private sealed record Batch(string Label, int[] Items);

    private sealed class RoundTrip
    {
        public int Conversions { get; private set; }
        public List<int> Seen { get; } = [];

        public int Seed(int input, PregelNodeContext context, CancellationToken token) => input + 10;

        public string Format(int input, PregelNodeContext context, CancellationToken token)
        {
            Conversions++;
            return (input * 2).ToString();
        }

        public int Parse(string value, PregelNodeContext context, CancellationToken token)
        {
            Conversions++;
            return int.Parse(value);
        }

        public void Observe(int value, PregelNodeContext context, CancellationToken token) => Seen.Add(value);
    }

    private sealed class Branches
    {
        public int Left { get; private set; }
        public int Right { get; private set; }
        public bool GatedRan { get; private set; }

        public int AddOne(int value, PregelNodeContext context, CancellationToken token) => value + 1;
        public int AddTwo(int value, PregelNodeContext context, CancellationToken token) => value + 2;
        public void ObserveLeft(int value, PregelNodeContext context, CancellationToken token) => Left = value;
        public void ObserveRight(int value, PregelNodeContext context, CancellationToken token) => Right = value;
        public void Gated(int value, PregelNodeContext context, CancellationToken token) => GatedRan = true;
    }

    private sealed class Cleanup(bool fail)
    {
        public PregelOutcome<int, string?>? Outcome { get; private set; }

        public int SeedOrFail(int input, PregelNodeContext context, CancellationToken token) =>
            fail ? throw new IOException("synthetic failure") : input + 1;

        public void Capture(PregelOutcome<int, string?> outcome, PregelNodeContext context, CancellationToken token) =>
            Outcome = outcome;
    }
}
