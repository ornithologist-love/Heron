namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel-engine.md")]
public sealed class ProgressTests
{
    [Fact]
    public async Task ProgressIsTypedNormalizedAndDoesNotCompleteAFailingNode()
    {
        var observer = new Observer();
        var graph = Pregel.Define<PregelUnit>("progress").Then(ReportAndFail).Compile();
        var result = await graph.RunAsync(PregelUnit.Value, new(Observer: observer));
        Assert.Equal(PregelRunStatus.Failed, result.Status);
        Assert.Equal(new double?[] { null, 0, .42, 1, null }, observer.Progress.Select(p => p.Fraction));
        Assert.All(observer.Events, e =>
        {
            Assert.Equal(result.RunId, e.RunId);
            Assert.Equal(Pregel.NodeId(ReportAndFail), e.NodeId);
            Assert.NotNull(e.AttemptId);
            Assert.True(e.Sequence > 0);
        });
        Assert.Equal("copying", observer.Progress[2].Detail);
    }

    [Fact]
    public async Task NestedProgressIsMappedToOuterNodeIdentity()
    {
        var observer = new Observer();
        var graph = Pregel.Define<PregelUnit>("outer").Then(Outer).Compile();
        var result = await graph.RunAsync(PregelUnit.Value, new(Observer: observer));
        Assert.Equal(PregelRunStatus.Completed, result.Status);
        Assert.Null(observer.Progress[0].Fraction);
        var value = observer.Events[1];
        Assert.Equal(result.RunId, value.RunId);
        Assert.Equal(Pregel.NodeId(Outer), value.NodeId);
        Assert.Equal(.25, observer.Progress[1].Fraction);
    }

    private static void ReportAndFail(PregelUnit _, PregelNodeContext context, CancellationToken token)
    {
        context.ReportProgress(null, "starting");
        context.ReportProgress(-1);
        context.ReportProgress(.42, "copying");
        context.ReportProgress(2);
        context.ReportProgress(double.NaN);
        throw new InvalidOperationException("failed after reporting progress");
    }

    private static void Inner(PregelUnit _, PregelNodeContext context, CancellationToken token) =>
        context.ReportProgress(.25, "nested");

    private static async Task Outer(PregelUnit _, PregelNodeContext context, CancellationToken token)
    {
        var graph = Pregel.Define<PregelUnit>("inner").Then(Inner).Compile();
        await graph.RunAsync(PregelUnit.Value, new(Observer: new PregelProgressForwarder(context)), token);
    }

    private sealed class Observer : PregelRunObserver
    {
        public List<PregelRunEvent> Events { get; } = [];
        public List<PregelProgress> Progress { get; } = [];
        protected override void OnNodeProgress(PregelRunEvent value, PregelProgress progress)
        {
            Events.Add(value);
            Progress.Add(progress);
        }
    }
}
