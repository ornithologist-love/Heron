namespace Heron.Pregel;

/// <summary>The graph body's outcome before cleanup, including its final typed value if committed.</summary>
public sealed record PregelOutcome<TInput, TValue>(
    TInput Input, bool HasValue, TValue? Value, PregelRunStatus Status, PregelError? Error);

public sealed partial class PregelFlow<TInput, TCurrent>
{
    /// <summary>Groups nodes and optionally skips the entire group while preserving its input.</summary>
    public PregelFlow<TInput, TNext> Stage<TNext>(string id, string title,
        Func<PregelFlow<TInput, TCurrent>, PregelFlow<TInput, TNext>> body,
        Func<TCurrent, bool>? when = null, string skipReason = "阶段运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureStageBoundary();
        if (when is not null && typeof(TNext) != typeof(TCurrent))
            throw new InvalidOperationException("A conditional Stage must preserve the flow type.");
        PregelId.Validate(id, nameof(id));
        var enabled = _builder.Output($"stage:{id}:enabled", true);
        var entry = Append($"stage:{id}:entry", (context, _) =>
        {
            context.Write(enabled, when?.Invoke(_current.Read(context)) ?? true);
            return ValueTask.CompletedTask;
        });
        var start = new PregelFlow<TInput, TCurrent>(_builder, _input, _current, _earlyReturns, entry);
        var end = ConfigureStage(id, title, entry, enabled, skipReason,
            () => start.GetBodyEnd(body(start), entry));

        if (typeof(TNext) != typeof(TCurrent) || end._current.Id == _current.Id)
            return new(_builder, _input, end._current, _earlyReturns, end._tail);

        // A round trip through another type has a different result slot. The join must
        // leave the original slot alone when the body was skipped and produced no value.
        var output = (PregelFlowSlot<TNext>)(object)_current;
        var join = _builder.Node($"stage:{id}:result", (context, _) =>
        {
            var value = end._current.Snapshot(context);
            if (value.IsSet) output.Write(context, value.Value!);
            return ValueTask.CompletedTask;
        });
        end._current.DeclareRead(_builder, join);
        _builder.AddEdge(end._tail!, join);
        MarkRouting(join, alwaysRuns: true);
        return new(_builder, _input, output, _earlyReturns, join);
    }

    /// <summary>Runs a detached cleanup stage with the original input and committed final value.</summary>
    public PregelFlow<TInput, TCurrent> Finally(
        Action<PregelFlow<TInput, PregelOutcome<TInput, TCurrent>>> body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureStageBoundary();
        if (_tail is null || _earlyReturns is null || _builder.Finally is not null)
            throw new InvalidOperationException("Finally requires a graph body and can only be declared once.");
        RequireFinalChain(_tail);

        var before = _builder.DefinedNodeIds.ToHashSet();
        var enabled = _builder.Output("stage:finally:enabled", true);
        var outcome = new PregelFlowSlot<PregelOutcome<TInput, TCurrent>>(_builder, "flow:outcome");
        var entry = _builder.Node("stage:finally:entry", (context, _) =>
        {
            var result = context.BodyResult!;
            var value = _current.Snapshot(context);
            outcome.Write(context, new(context.Read(_input), value.IsSet, value.Value, result.Status, result.Error));
            return ValueTask.CompletedTask;
        });
        _builder.DeclareRead(entry, _input);
        _current.DeclareRead(_builder, entry);
        MarkRouting(entry, alwaysRuns: true);

        var start = new PregelFlow<TInput, PregelOutcome<TInput, TCurrent>>(_builder, _input, outcome, null, entry);
        ConfigureStage("finally", "Finally", entry, enabled, "收尾条件不满足", () =>
        {
            body(start);
            return true;
        });
        _builder.SetFinally(entry.Id, _builder.DefinedNodeIds.Where(node => !before.Contains(node)).ToArray(),
            timeout ?? TimeSpan.FromMinutes(2));
        return new(_builder, _input, _current, _earlyReturns, _tail);
    }

    /// <summary>Runs fixed branches with isolated values. The join keeps the original value.</summary>
    public PregelFlow<TInput, TCurrent> Parallel(
        params Func<PregelFlow<TInput, TCurrent>, PregelFlow<TInput, TCurrent>>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length < 2) throw new ArgumentException("Parallel requires at least two branches.", nameof(branches));
        var prefix = $"parallel:{_builder.DefinedNodeIds.Count}";
        var fork = Append($"{prefix}:fork", (_, _) => ValueTask.CompletedTask);
        MarkRouting(fork, alwaysRuns: true);
        var arrivals = Enumerable.Range(0, branches.Length).Select(i => new NodeId($"{prefix}:arrive:{i}")).ToArray();
        var barrier = _builder.NamedBarrierChannel($"{prefix}:complete", arrivals);
        for (var i = 0; i < branches.Length; i++)
        {
            var end = BuildParallelBranch(fork, $"{prefix}:branch:{i}", branches[i]);
            var arrival = _builder.Node(arrivals[i].Value, (context, _) =>
            {
                context.Write(barrier, PregelUnit.Value);
                return ValueTask.CompletedTask;
            });
            _builder.AddEdge(end, arrival);
            MarkRouting(arrival, alwaysRuns: true);
        }
        var join = _builder.Node($"{prefix}:join", (_, _) => ValueTask.CompletedTask);
        _builder.TriggerOn(join, barrier);
        MarkRouting(join, alwaysRuns: true);
        return new(_builder, _input, _current, _earlyReturns, join);
    }

    private NodeRef<PregelUnit> BuildParallelBranch(NodeRef<PregelUnit> fork, string id,
        Func<PregelFlow<TInput, TCurrent>, PregelFlow<TInput, TCurrent>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var input = new PregelFlowSlot<TCurrent>(_builder, $"flow:{id}");
        var selection = _builder.Output($"{id}:selected", new PregelBranchSelection(null, null));
        var conditions = new List<Func<TCurrent, string?>>();
        Func<TCurrent, string?>[] tests = [];
        var entry = _builder.Node($"{id}:entry", (context, _) =>
        {
            var value = _current.Read(context);
            input.Write(context, value);
            var reason = tests.Select(test => test(value)).FirstOrDefault(reason => reason is not null);
            context.Write(selection, new(reason is null ? "run" : null, reason));
            return ValueTask.CompletedTask;
        });
        _current.DeclareRead(_builder, entry);
        _builder.AddEdge(fork, entry);
        MarkRouting(entry, alwaysRuns: false);
        var before = _builder.DefinedNodeIds.ToHashSet();
        var start = new PregelFlow<TInput, TCurrent>(_builder, _input, input, null, entry, branchConditions: conditions);
        var end = start.GetBodyEnd(body(start), entry);
        tests = conditions.ToArray();
        if (tests.Length > 0)
            _builder.AddRouteScope(_builder.DefinedNodeIds.Where(node => !before.Contains(node)), entry.Id, selection, "run");
        return end._tail!;
    }

    private void EnsureStageBoundary()
    {
        if (_builder.StageRoutingNodes is not null) throw new InvalidOperationException("Nested stages are not supported.");
    }

    private T ConfigureStage<T>(string id, string title, NodeRef<PregelUnit> entry,
        PregelVariable<bool> enabled, string skipReason, Func<T> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(skipReason);
        var before = _builder.DefinedNodeIds.ToHashSet();
        var routing = new HashSet<NodeId>();
        _builder.StageRoutingNodes = routing;
        T result;
        try { result = build(); }
        finally { _builder.StageRoutingNodes = null; }

        var added = _builder.DefinedNodeIds.Where(node => !before.Contains(node)).ToArray();
        var members = added.Where(node => !routing.Contains(node)).ToArray();
        if (members.Length == 0) throw new ArgumentException("A stage must contain a node.", nameof(build));
        _builder.AddStage(new(id, title, entry.Id, Array.AsReadOnly(members), skipReason), enabled,
            added.Where(node => !_builder.BarrierControlNodes.Contains(node)));
        return result;
    }

    private PregelFlow<TInput, TNext> GetBodyEnd<TNext>(PregelFlow<TInput, TNext>? end, NodeRef<PregelUnit> entry)
    {
        if (end is null || !ReferenceEquals(end._builder, _builder) || end._tail is null || end._tail.Id == entry.Id)
            throw new ArgumentException("The body must return its final chain.", nameof(end));
        RequireFinalChain(end._tail);
        return end;
    }

    private void MarkRouting(NodeRef<PregelUnit> node, bool alwaysRuns)
    {
        _builder.StageRoutingNodes?.Add(node.Id);
        if (alwaysRuns) _builder.BarrierControlNodes.Add(node.Id);
    }
}
