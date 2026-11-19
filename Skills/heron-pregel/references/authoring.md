# Authoring graphs

## Files and entry points

These namespaces are imported automatically: `System`, `System.IO`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`, `System.Diagnostics`, `System.ComponentModel`, `Heron.Pregel`, `Heron.Pregel.Tooling` and `Heron.Pregel.Automation`. `ScriptDirectory` is available, and `#load` resolves relative to the current file. For JSON attributes add `#r "System.Text.Json"` and `using System.Text.Json.Serialization;`.

An entry script returns a compiled `PregelGraph`, `PregelGraph<TInput>`, `PregelGraph<TInput, TOutput>`, or an automation template. It never returns a `Task`, a run result or an uncompiled builder, and it never runs the graph itself.

A helper file starts with `// heron-pregel-script: support` and is not listed as an entry. An entry may declare a title:

```csharp
// heron-pregel-script-title: My flow
// heron-pregel-script-description: What it does and what it produces
#load "MyFlow.Graph.csx"
return new MyFlowGraph().Build();
```

`MyFlow.csx` and `MyFlow.Template.csx` side by side count as one entry, and the template wins. Do not `#load` a file with a top-level `return`; share a graph factory instead.

## Inputs

Input types have a parameterless constructor and public `init` or `set` properties. `[DisplayName]` marks a property as a field.

`Input.json` may declare defaults and choices:

```json
{
  "with": [
    { "mode": "normal", "value": [ { "value": "normal", "label": "Normal" }, { "value": "detailed", "label": "Detailed" } ] },
    { "enabled": true }
  ]
}
```

Each item in `with` declares exactly one parameter with its default, plus optional `value` choices. The default must be among the choices. Saved task parameters override defaults, so editing the JSON does not rewrite existing tasks.

## Stages and nodes

Use named methods with the typed DSL. The base node signature is `void Method(TInput, PregelNodeContext, CancellationToken)`. A node may return `TResult`, `Task` or `Task<TResult>`. Keep all three parameters even when one is unused, because the DSL overloads bind on them.

```csharp
var flow = Pregel.Define<MyInput>("MyFlow", "1");
return flow
    .Stage("reuse", "Reuse", s => s.Then(Reuse), when: input => input.UseLocal, skipReason: "Downloading instead")
    .Stage("download", "Download", s => s.Then(Download), when: input => !input.UseLocal, skipReason: "Reusing local copy")
    .Stage("execute", "Execute", s => s.Then(Execute))
    .Compile();
```

A stage's condition is checked once on entry; when false its nodes are skipped and the graph continues. Use `.When(predicate, "reason")` for a single optional node.

A node's return value feeds the next node, or bind explicitly with a selector. For shared data use `flow.State<TState>("state", initial)`. State must be serializable: records and value types, never `Process`, `Stream` or delegates. For a process, keep its ID and start time.

Use `.Parallel(a => a.Then(A), b => b.Then(B))` for independent branches, writing to separate state slots. Never draw dependent operations as parallel. A `When` at the start of a branch gates the whole branch. A `When` after a `Then` gates only that node:

```csharp
stage.Parallel(
    node => node.When(i => i.InstallNode, "Node.js not selected")
        .Then(DetectNode)
        .Then(InstallNode).When(i => !_probes["Node"].Ready, "Node.js already installed")
        .Then(VerifyNode),
    git => git.When(i => i.InstallGit, "Git not selected")
        .Then(DetectGit).Then(InstallGit).Then(VerifyGit));
```

When the branches come from data, run one branch per item. `Parallel` starts them together and `ForEach` runs them one after another. Each item is its own child run:

```csharp
stage.Parallel("reports", run => run.Units.Where(unit => unit.Tier == "epic"),
    key: unit => $"{unit.Date}/{unit.Scene}/{unit.Tier}",
    unit => unit.Graph("report", reportGraph).SkipIf(report => report.SkipReason));
```

- The `items` lambda picks one collection with at most one `Where`. For grouping, building new objects or reading state, pass a named method instead of the lambda, or compute the list in an upstream node. It must be pure: no IO, clock or randomness.
- `key` is the stable business identity of an item and is used after a restart. Never use an index or a GUID. `title:` and `target:` are optional.
- Filter with `Where` to leave an item out entirely. Start the branch with `When` to show it as skipped.
- `SkipIf` directly follows a `Graph` call and marks it skipped when it returns a reason.
- Do not write `Mutate` or `PregelMutationItem` in new graphs. They remain only for scripts that are already deployed.

Call a compiled graph with `.Graph("child", childGraph)`. The current value must match the called graph's input type. If it needs conversion, do that in a named `Then` node before the call. The engine records the child run and passes its result to the next node.

## Human review

Interrupt in a node, and read the decision when the run resumes:

```csharp
if (!context.HasResumeValue)
{
    context.Interrupt("Apply this change?", summary);
    return;
}
var answer = context.ResumeValue<string>();
```

The run reaches `AwaitingReview` and the process may exit. See `examples/review`.

## Locks

```csharp
[MutexRule("output:{OutputDirectory}")]
sealed class MyFlowGraph
{
    // Inside Build: .MutexRules(typeof(MyFlowGraph)). The attribute alone wires nothing.
}
```

Lock keys come from the initial input. Use the same absolute path or real identity across entry points, not a task name. The default is exclusive. A parent graph declares the locks for its whole segment.

## Running tools

```csharp
async Task Execute(MyInput input, PregelNodeContext context, CancellationToken token)
{
    var start = new ProcessStartInfo(input.Executable) { WorkingDirectory = input.WorkingDirectory, CreateNoWindow = true };
    start.ArgumentList.Add("--output");
    start.ArgumentList.Add(input.OutputDirectory);
    await ProcessExecution.RunCheckedAsync(start, context, token);
}
```

Use `ArgumentList` rather than building a shell string. A non-zero exit fails the node, and cancelling ends the process tree the node started. `RunCheckedAsync` logs the arguments, so never pass secrets on the command line.

## Finally

```csharp
.Finally(s => s.Then(Stop).Then(Archive), TimeSpan.FromMinutes(5))
```

Success, failure, cancellation and early return all enter `Finally`; a human interrupt waits first. Cleanup must tolerate running twice, because external effects and checkpoints are not one atomic transaction. Only independent cleanup nodes may use `ContinueOnError`.

## Templates

A plain graph already gets a form and run history. Add `AutomationGraphTemplate<TInput>` only for dynamic choices, validation, parameter previews or result summaries. Override `CreateGraph`, and as needed `Validate`, `Complete`, `ListFieldChoicesAsync`, `PrepareGraphInput` and `RenderParameters`. The base class already handles steps, durable runs, interrupts and finalization, so do not create a `PregelEngine` or poll records yourself.
