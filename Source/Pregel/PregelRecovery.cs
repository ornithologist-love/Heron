namespace Heron.Pregel;

public sealed record PregelRecoveryDefinition(NodeId Id, PregelNodeMetadata Metadata);

internal sealed record PregelRecovery(PregelRecoveryDefinition Definition,
    Func<CompiledPregelGraph, PregelRunResult, PregelNodeContext, CancellationToken, Task> Execute);
