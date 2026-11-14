namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class DslEntryArgumentAndEndTests
{
    [Fact]
    public async Task EntryArgument_IsDeliveredInOrderThroughTheChain()
    {
        var tasks = new DslTasks();
        var observed = new List<string>();
        var graph = Pregel
            .Define("entry-argument")
            .StartExecution(tasks.First, "A", tasks.Executor(observed))
            .ThenExecution(tasks.Second, "B")
            .Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Equal("A", "B");
    }

    [Fact]
    public async Task FlowEnd_InMiddleNode_StopsTheChainAndCompletes()
    {
        var tasks = new DslTasks();
        var observed = new List<string>();
        var graph = Pregel
            .Define("flow-end-middle")
            .StartExecution(tasks.First, "A", tasks.FlowExecutor(observed, endAt: "B"))
            .ThenExecution(tasks.Second, "B")
            .ThenExecution(tasks.Third, "C")
            .Compile();

        var result = await new PregelEngine().StartAsync(graph);

        result.Status.Should().Be(PregelRunStatus.Completed);
        result.Error.Should().BeNull();
        observed.Should().Equal("A", "B");
    }

    [Fact]
    public async Task Variable_IsSuppliedPerRun_AndReadByEntryAndLaterNodes()
    {
        var tasks = new DslTasks();
        var observed = new List<string>();
        var def = Pregel.Define("variable-read");
        var path = def.Variable<string>("path");
        var graph = def
            .StartExecution(tasks.First, path, tasks.Executor(observed))
            .ThenExecution(tasks.Second, path)
            .ThenExecution(tasks.Third, "C")
            .Compile();

        var first = await new PregelEngine().StartAsync(graph, new PregelVariables().Set(path, "P1"));
        var second = await new PregelEngine().StartAsync(graph, new PregelVariables().Set(path, "P2"));

        first.Status.Should().Be(PregelRunStatus.Completed);
        second.Status.Should().Be(PregelRunStatus.Completed);
        observed.Should().Equal("P1", "P1", "C", "P2", "P2", "C");
    }

    [Fact]
    public async Task Variable_ValueSurvivesCrashAndResume()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heron-pregel-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var tasks = new DslTasks();
            var observed = new List<string>();
            var def = Pregel.Define("variable-resume");
            var path = def.Variable<string>("path");
            var graph = def
                .StartExecution(tasks.First, path, tasks.Executor(observed))
                .ThenExecution(tasks.Second, path)
                .Compile();
            var runId = new PregelRunId("variable-resume-run");

            var first = async () => await new PregelEngine(new FilePregelCheckpointStore(directory, new ThrowOnceAfterOutcome()))
                .StartAsync(graph, runId, new PregelVariables().Set(path, "Kept"));
            await first.Should().ThrowAsync<PregelException>()
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

    private sealed class DslTasks
    {
        public Func<string, CancellationToken, ValueTask<PregelFlow>> ReturnFlow(
            PregelFlow flow,
            ICollection<string>? observed = null) =>
            (value, _) =>
            {
                observed?.Add(value);
                return ValueTask.FromResult(flow);
            };

        public string Zero() => "0";

        public string First(string value) => value;

        public string Second(string value) => value;

        public string Third(string value) => value;

        public Func<string, CancellationToken, ValueTask> Executor(ICollection<string> observed) =>
            (value, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed.Add(value);
                return ValueTask.CompletedTask;
            };

        public Func<string, CancellationToken, ValueTask<PregelFlow>> FlowExecutor(
            ICollection<string> observed,
            string? endAt = null) =>
            (value, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed.Add(value);
                return ValueTask.FromResult(value == endAt ? PregelFlow.End : PregelFlow.Continue);
            };
    }
}
