namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class DslEntryArgumentAndEndTests
{
    [Fact]
    public async Task EntryInput_IsDeliveredInOrderThroughTheChain()
    {
        var observed = new List<string>();
        var tasks = new DslTasks(observed);
        var graph = Pregel
            .Define<string>("entry-input")
            .Then(tasks.First)
            .Then(tasks.Second)
            .Compile();

        var result = await graph.RunAsync("A");

        result.Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Equal("A", "A");
    }

    [Fact]
    public async Task ContextEnd_InMiddleNode_StopsTheChainAndCompletes()
    {
        var observed = new List<string>();
        var tasks = new DslTasks(observed, endAtSecond: true);
        var graph = Pregel
            .Define<string>("flow-end-middle")
            .Then(tasks.First)
            .Then(tasks.Second)
            .Then(tasks.Third)
            .Compile();

        var result = await graph.RunAsync("A");

        result.Status.Should().Be(PregelRunStatus.Completed);
        result.Error.Should().BeNull();
        observed.Should().Equal("A", "A");
    }

    [Fact]
    public async Task Variable_IsSuppliedPerRun_AndReadByEntryAndLaterNodes()
    {
        var observed = new List<string>();
        var builder = new PregelGraphBuilder("variable-read");
        var path = builder.Variable<string>("path");
        var first = builder.Node("first", (context, _) => { observed.Add(context.Read(path)); return ValueTask.CompletedTask; });
        var second = builder.Node("second", (context, _) => { observed.Add(context.Read(path)); return ValueTask.CompletedTask; });
        var third = builder.Node("third", "C", (arg, _, _) => { observed.Add(arg); return ValueTask.CompletedTask; });
        var graph = builder.SetEntry(first).AddEdge(first, second).AddEdge(second, third).Compile();

        var firstRun = await new PregelEngine().StartAsync(graph, new PregelVariables().Set(path, "P1"));
        var secondRun = await new PregelEngine().StartAsync(graph, new PregelVariables().Set(path, "P2"));

        firstRun.Status.Should().Be(PregelRunStatus.Completed);
        secondRun.Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Equal("P1", "P1", "C", "P2", "P2", "C");
    }

    [Fact]
    public async Task Variable_ValueSurvivesCrashAndResume()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heron-pregel-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var observed = new List<string>();
            var builder = new PregelGraphBuilder("variable-resume");
            var path = builder.Variable<string>("path");
            var first = builder.Node("first", (context, _) => { observed.Add(context.Read(path)); return ValueTask.CompletedTask; });
            var second = builder.Node("second", (context, _) => { observed.Add(context.Read(path)); return ValueTask.CompletedTask; });
            var graph = builder.SetEntry(first).AddEdge(first, second).Compile();
            var runId = new PregelRunId("variable-resume-run");

            var firstRun = async () => await new PregelEngine(new FilePregelCheckpointStore(directory, new ThrowOnceAfterOutcome()))
                .StartAsync(graph, runId, new PregelVariables().Set(path, "Kept"));
            await firstRun.Should().ThrowAsync<PregelException>()
                .Where(exception => exception.Code == "simulated_crash");

            var resumed = await new PregelEngine(new FilePregelCheckpointStore(directory)).ResumeAsync(graph, runId);

            resumed.Status.Should().Be(PregelRunStatus.Completed);
            observed.Should().Equal("Kept", "Kept");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ThrowOnceAfterOutcome : IFilePregelStoreFaultInjector
    {
        private int _armed = 1;

        public ValueTask OnFaultPointAsync(FilePregelStoreFaultPoint faultPoint, CancellationToken cancellationToken)
        {
            if (faultPoint == FilePregelStoreFaultPoint.AfterOutcomeWriteBeforeBarrier
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new PregelException("simulated_crash", "Crash after End outcome was persisted.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class DslTasks(List<string> observed, bool endAtSecond = false)
    {
        public void First(string value, PregelNodeContext context, CancellationToken token) => observed.Add(value);

        public void Second(string value, PregelNodeContext context, CancellationToken token)
        {
            observed.Add(value);
            if (endAtSecond) context.End();
        }

        public void Third(string value, PregelNodeContext context, CancellationToken token) => observed.Add(value);
    }
}
