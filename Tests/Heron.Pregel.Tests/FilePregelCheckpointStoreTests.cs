using System.Text.Json;

namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class FilePregelCheckpointStoreTests
{
    [Fact]
    public async Task InterruptedRun_ResumesAfterStoreAndEngineAreRecreated()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var accepted = false;
            var builder = new PregelGraphBuilder("file-interrupt-resume");
            var node = builder.Node("node", (context, _) =>
            {
                if (!context.HasResumeValue)
                {
                    context.Interrupt("pause");
                    return ValueTask.CompletedTask;
                }

                accepted = context.ResumeValue<bool>();
                return ValueTask.CompletedTask;
            });
            var graph = builder.SetEntry(node).Compile();
            var first = await new PregelEngine(new FilePregelCheckpointStore(directory))
                .StartAsync(graph);

            var secondEngine = new PregelEngine(new FilePregelCheckpointStore(directory));
            var completed = await secondEngine.ResumeWithValueAsync(
                graph,
                first.RunId,
                first.Interrupt!.InterruptId,
                true);

            completed.Status.Should().Be(PregelRunStatus.Completed);
            accepted.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(FilePregelStoreFaultPoint.BeforeOutcomeWrite), 2)]
    [InlineData(nameof(FilePregelStoreFaultPoint.AfterOutcomeWriteBeforeBarrier), 1)]
    [InlineData(nameof(FilePregelStoreFaultPoint.AfterBarrierCheckpointWriteBeforeManifest), 1)]
    [InlineData(nameof(FilePregelStoreFaultPoint.AfterManifestWriteBeforeOutcomeCleanup), 1)]
    public async Task CrashAtDurabilityBoundary_RecoversOnlyWholeCheckpoint(
        string faultPointName,
        int expectedExecutions)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var faultPoint = Enum.Parse<FilePregelStoreFaultPoint>(faultPointName);
            var executions = 0;
            var runId = new PregelRunId($"fault-{faultPoint}");
            var builder = new PregelGraphBuilder($"fault-{faultPoint}");
            var node = builder.Node("node", (_, _) =>
            {
                executions++;
                return ValueTask.CompletedTask;
            });
            var graph = builder.SetEntry(node).Compile();
            var injector = new ThrowOnceFaultInjector(faultPoint);
            var firstEngine = new PregelEngine(new FilePregelCheckpointStore(directory, injector));

            var start = async () => await firstEngine.StartAsync(graph, runId);
            await start.Should().ThrowAsync<PregelException>()
                .Where(exception => exception.Code == "simulated_crash");

            var recoveredStore = new FilePregelCheckpointStore(directory);
            var durable = await recoveredStore.LoadLatestAsync(runId);
            durable.Should().NotBeNull();
            durable!.Status.Should().Be(PregelRunStatus.Running);
            await recoveredStore.CleanupConsumedOutcomesAsync(runId);

            var completed = await new PregelEngine(recoveredStore).ResumeAsync(graph, runId);

            completed.Status.Should().Be(PregelRunStatus.Completed);
            executions.Should().Be(expectedExecutions);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptReferencedCheckpoint_IsRejectedInsteadOfReturningPartialState()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var builder = new PregelGraphBuilder("corrupt-checkpoint");
            var node = builder.Node("node", static (_, _) => ValueTask.CompletedTask);
            var graph = builder.SetEntry(node).Compile();
            var result = await new PregelEngine(new FilePregelCheckpointStore(directory))
                .StartAsync(graph);
            var manifestPath = Directory.GetFiles(directory, "manifest.json", SearchOption.AllDirectories).Single();
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var checkpointFile = manifest.RootElement.GetProperty("CheckpointFile").GetString()!;
            var checkpointPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "checkpoints", checkpointFile);
            await File.WriteAllTextAsync(checkpointPath, "{");

            var load = async () => await new FilePregelCheckpointStore(directory).LoadLatestAsync(result.RunId);

            await load.Should().ThrowAsync<PregelException>()
                .Where(exception => exception.Code == "store_read_failed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"heron-pregel-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowOnceFaultInjector(FilePregelStoreFaultPoint selectedPoint)
        : IFilePregelStoreFaultInjector
    {
        private int _armed = 1;

        public ValueTask OnFaultPointAsync(
            FilePregelStoreFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (faultPoint == selectedPoint && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new PregelException("simulated_crash", $"Crash at {faultPoint}.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
