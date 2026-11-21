namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-authoring.md")]
public sealed class DslContractTests
{
    [Fact]
    public async Task Then_ResolvesAllFourNamedMethodShapes()
    {
        var steps = new PipelineSteps();
        var retry = new PregelRetryPolicy(MaxAttempts: 2);
        var graph = Pregel.Define<int>("dsl-overloads")
            .Then(steps.RecordSync)
            .Then(steps.RecordAsync).Retry(retry)
            .Then(steps.ToSeedSync)
            .Then(steps.ObserveResultSync)
            .Then(steps.ObserveResultAsync).Retry(retry)
            .Then(steps.IncrementSameTypeSync)
            .Then(steps.IncrementSameTypeAsync).Retry(retry)
            .Then(steps.FormatAsync)
            .Then(steps.SuffixSync).Retry(retry)
            .Compile();

        var result = await graph.RunAsync(10);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be("12!");
        steps.Log.Should().Equal("sync:10", "async:10", "res-sync:10", "res-async:10");
    }

    [Fact]
    public async Task SameTypeSyncResultStep_WhenSkipped_PreservesCommittedResult()
    {
        var steps = new PipelineSteps();
        var graph = Pregel.Define<int>("dsl-same-type-sync-skip")
            .Then(steps.ToSeedSync)
            .Then(steps.IncrementSameTypeSync).When(value => value != 7, "skip increment for 7")
            .Then(steps.ObserveResultSync)
            .Compile();

        var result = await graph.RunAsync(7);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be(7);
        steps.Log.Should().Equal("res-sync:7");
    }

    [Fact]
    public void TypeChangingNodes_RejectSkippingAndContinuingOnError()
    {
        var steps = new PipelineSteps();
        var flow = Pregel.Define<int>("required-result").Then(steps.FormatAsync);

        var skip = () => flow.When(_ => false);
        var continueOnError = () => flow.ContinueOnError();

        skip.Should().Throw<InvalidOperationException>();
        continueOnError.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task RetryAndContinueOnError_KeepTheLastCommittedValue()
    {
        var steps = new PipelineSteps();
        var graph = Pregel.Define<int>("failed-result")
            .Then(steps.IncrementSameTypeSync)
            .Then(steps.Fail).Retry(new(MaxAttempts: 2)).ContinueOnError()
            .Then(steps.ObserveResultSync)
            .Compile();

        var run = graph.Prepare(7);
        var result = await run.RunAsync();

        result.Status.Should().Be(PregelRunStatus.Completed);
        steps.Attempts.Should().Be(2);
        steps.Log.Should().Equal("res-sync:8");
        run.Read().Should().Be(8);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(2, "2!")]
    public async Task ReturnIf_UsesTheNewlyProducedValueAndReturnsTheDeclaredOutput(int input, string expected)
    {
        var steps = new PipelineSteps();
        var graph = Pregel.Define<int>("early-result")
            .Then(steps.FormatAsync).ReturnIf(value => value == "1")
            .Then(steps.SuffixSync)
            .Compile();

        var result = await graph.RunAsync(input);

        result.Status.Should().Be(PregelRunStatus.Completed);
        graph.Read(result).Should().Be(expected);
    }

    [Fact]
    public void ReturnIf_RejectsAnEarlyExitThatCannotSupplyTheFinalResult()
    {
        var steps = new PipelineSteps();
        var flow = Pregel.Define<int>("wrong-early-result")
            .Then(steps.IncrementSameTypeSync).ReturnIf(value => value > 0)
            .Then(steps.FormatAsync);
        var compileString = () => flow.Compile();
        compileString.Should().Throw<InvalidOperationException>().WithMessage("ReturnIf*final result*");

        var compileInt = () => flow.Then(steps.ParseSync).Compile();
        compileInt.Should().Throw<InvalidOperationException>().WithMessage("ReturnIf*final result*");
    }

    [Fact]
    public void Then_RejectsAnonymousNodes()
    {
        var flow = Pregel.Define<int>("anonymous-node");
        var define = () => flow.Then((int value, PregelNodeContext context, CancellationToken token) => value + 1);

        define.Should().Throw<PregelException>().Which.Code.Should().Be("unstable_task_method");
    }

    [Fact]
    public async Task Then_CanCommitNullAsAValue()
    {
        var steps = new PipelineSteps();
        var graph = Pregel.Define<string>("nullable-value").Then(steps.Clear).Compile();
        graph.Read(await graph.RunAsync("initial")).Should().BeNull();
    }

    [Fact]
    public async Task Then_CarriesTheInputFieldsNeededAfterATypeChange()
    {
        var steps = new StateSteps();
        var graph = Pregel.Define<StateInput>("dsl-state-as-current-value")
            .Stage("state", "State", stage => stage
                .Then(steps.AddTenSync)
                .Then(steps.DoubleAsync)
                .Then(steps.CheckLabelSync)
                .Then(steps.CheckLabelAsync)
                .Then(steps.FinishSync))
            .Then(steps.AfterResultSync)
            .Compile();

        var result = await graph.RunAsync(new StateInput(5, "ok"));

        result.Status.Should().Be(PregelRunStatus.Completed);
        var output = graph.Read(result);
        output.Input.Base.Should().Be(30);
        output.Message.Should().Be("done:ok");
        steps.Labels.Should().Equal("sync:ok", "async:ok", "after:ok");
    }

    [Fact]
    public async Task RootLevel_GraphAndMap_WorkWithoutSeparateStartApi()
    {
        var child = Pregel.Define<int>("root-child").Then(DoubleSync).Compile();
        var rootGraph = Pregel.Define<int>("root-graph-call").Graph("child", child).Compile();
        var rootResult = await rootGraph.RunAsync(4);
        rootResult.Status.Should().Be(PregelRunStatus.Completed);
        rootGraph.Read(rootResult).Should().Be(8);

        var itemsGraph = Pregel.Define<int[]>("root-parallel")
            .Map("items", items => items, item => item.ToString(), branch => branch.Graph("child", child),
                PregelMutationMode.Parallel)
            .Compile();
        var itemsResult = await itemsGraph.RunAsync([2, 3]);
        itemsResult.Status.Should().Be(PregelRunStatus.Completed);
        itemsGraph.Read(itemsResult).Items.Should().Equal(4, 6);
    }

    [Fact]
    public async Task PregelUnitGraph_AdaptsAsNoInputGraphInDynamicPregelGraph()
    {
        var ran = false;
        var graph = Pregel.Define<PregelUnit>("unit-graph")
            .Stage("run", "Run", stage => stage.Then(new UnitStep(() => ran = true).Run))
            .Compile();

        var dynamicGraph = DynamicPregelGraph.Adapt(graph);
        dynamicGraph.InputType.Should().BeNull();
        dynamicGraph.OutputType.Should().Be(typeof(PregelUnit));

        var engine = new PregelEngine(new InMemoryPregelCheckpointStore());
        var result = await dynamicGraph.Prepare(options: new() { Engine = engine }).RunAsync();
        result.Status.Should().Be(PregelRunStatus.Completed);
        (await engine.GetStateAsync(result.RunId)).Should().NotBeNull();
        dynamicGraph.Read(result).Should().Be(PregelUnit.Value);
        ran.Should().BeTrue();
    }

    [Fact]
    public async Task DefineWithoutInput_UsesTheSameFlow()
    {
        var ran = false;
        var graph = Pregel.Define("unit-entry")
            .Then(new UnitStep(() => ran = true).Run)
            .Compile();
        var result = await graph.RunAsync(PregelUnit.Value);
        result.Status.Should().Be(PregelRunStatus.Completed);
        ran.Should().BeTrue();
        graph.Read(result).Should().Be(PregelUnit.Value);
    }

    private static int DoubleSync(int value, PregelNodeContext context, CancellationToken token) => value * 2;

    private sealed record StateInput(int Base, string Label);
    private sealed record StateResult(StateInput Input, string Message);

    private sealed class UnitStep(Action onRun)
    {
        public void Run(PregelUnit _, PregelNodeContext context, CancellationToken token) => onRun();
    }

    private sealed class PipelineSteps
    {
        public List<string> Log { get; } = [];
        public int Attempts { get; private set; }

        public void RecordSync(int input, PregelNodeContext context, CancellationToken token) => Log.Add($"sync:{input}");

        public Task RecordAsync(int input, PregelNodeContext context, CancellationToken token)
        {
            Log.Add($"async:{input}");
            return Task.CompletedTask;
        }

        public int ToSeedSync(int input, PregelNodeContext context, CancellationToken token) => input;

        public void ObserveResultSync(int value, PregelNodeContext context, CancellationToken token) => Log.Add($"res-sync:{value}");

        public Task ObserveResultAsync(int value, PregelNodeContext context, CancellationToken token)
        {
            Log.Add($"res-async:{value}");
            return Task.CompletedTask;
        }

        public int IncrementSameTypeSync(int value, PregelNodeContext context, CancellationToken token) => value + 1;

        public Task<int> IncrementSameTypeAsync(int value, PregelNodeContext context, CancellationToken token) => Task.FromResult(value + 1);

        public Task<string> FormatAsync(int value, PregelNodeContext context, CancellationToken token) => Task.FromResult(value.ToString());

        public string SuffixSync(string value, PregelNodeContext context, CancellationToken token) => value + "!";

        public int ParseSync(string value, PregelNodeContext context, CancellationToken token) => int.Parse(value);

        public string? Clear(string value, PregelNodeContext context, CancellationToken token) => null;

        public int Fail(int value, PregelNodeContext context, CancellationToken token)
        {
            Attempts++;
            throw new IOException("synthetic failure");
        }
    }

    private sealed class StateSteps
    {
        public List<string> Labels { get; } = [];

        public StateInput AddTenSync(StateInput state, PregelNodeContext context, CancellationToken token) =>
            state with { Base = state.Base + 10 };

        public Task<StateInput> DoubleAsync(StateInput state, PregelNodeContext context, CancellationToken token) =>
            Task.FromResult(state with { Base = state.Base * 2 });

        public void CheckLabelSync(StateInput state, PregelNodeContext context, CancellationToken token) =>
            Labels.Add($"sync:{state.Label}");

        public Task CheckLabelAsync(StateInput state, PregelNodeContext context, CancellationToken token)
        {
            Labels.Add($"async:{state.Label}");
            return Task.CompletedTask;
        }

        public StateResult FinishSync(StateInput input, PregelNodeContext context, CancellationToken token) =>
            new(input, $"done:{input.Label}");

        public void AfterResultSync(StateResult result, PregelNodeContext context, CancellationToken token) =>
            Labels.Add($"after:{result.Input.Label}");
    }
}
