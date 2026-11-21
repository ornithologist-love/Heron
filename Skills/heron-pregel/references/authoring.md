# Authoring graphs

## Files and entry points

These namespaces are imported automatically: `System`, `System.IO`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`, `System.Diagnostics`, `System.ComponentModel`, `Heron.Pregel`, `Heron.Pregel.Tooling` and `Heron.Pregel.Automation`. `ScriptDirectory` is available, and `#load` resolves relative to the current file. For JSON attributes add `#r "System.Text.Json"` and `using System.Text.Json.Serialization;`.

An entry script returns a compiled `PregelGraph<TInput, TOutput>` or an automation template. Start with `Pregel.Define<TInput>(id)` or use `Pregel.Define(id)` for a graph without input. The host executes the graph after loading the script.

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

Use named methods with the typed DSL. A `Then` node accepts the current value, `PregelNodeContext`, and `CancellationToken`. Return `void` or `Task` to keep the current value, or `TResult` / `Task<TResult>` to pass a result downstream.

```csharp
var flow = Pregel.Define<MyInput>("MyFlow");
return flow
    .Stage("reuse", "Reuse", s => s.Then(Reuse), when: input => input.UseLocal, skipReason: "Downloading instead")
    .Stage("download", "Download", s => s.Then(Download), when: input => !input.UseLocal, skipReason: "Reusing local copy")
    .Stage("execute", "Execute", s => s.Then(Execute))
    .Compile();
```

A stage's condition is checked once on entry. A conditional stage must end with its input type, and skipping it preserves the incoming value. Use `.When(predicate, "reason")` on a node that keeps the current type. Use `ReturnIf` after a node that produces the graph's final type to finish early based on that value.

Carry the data downstream nodes need in the current value. Use a serializable record to group fields, and return a new value when those fields change. For a process, keep its ID and start time.

## Parallel branches and collections

Use `.Parallel(a => a.Then(A), b => b.Then(B))` for independent fixed branches. Each branch ends with its input type. After the join, the next node receives the value from before the fork. A `When` at the start of a branch gates the whole branch. A `When` after a `Then` gates only that node.

```csharp
stage.Parallel(
    cache => cache.When(input => input.CheckCache, "Cache check disabled")
        .Then(CheckCache).Then(LogCache),
    output => output.Then(CheckOutput)
        .Then(LogOutput).When(input => input.Verbose, "Verbose logging disabled"));
```

When branches come from data, use `ForEach` to run one branch per item and keep the current value. Use `Map` when the next node needs the results. Both run sequentially by default and accept `mode: PregelMutationMode.Parallel`.

```csharp
return Pregel.Define<Batch>("MeasureBatch")
    .Map("measure", batch => batch.Items.Where(item => item.Enabled),
        key: item => item.Id,
        body: item => item.Then(Measure),
        mode: PregelMutationMode.Parallel)
    .Then(Total)
    .Compile();

int Total(PregelMapResult<Batch, int> mapped, PregelNodeContext context, CancellationToken token) =>
    mapped.State.BaseTotal + mapped.Items.Sum();
```

- `Measure` receives one item and returns an `int`. `Total` receives the incoming batch in `State` and its branch results in `Items`, ordered by selection.
- Use a stable `key` for each item so its child run can resume. Use `Where` to omit an item and a branch-opening `When` to record it as skipped.
- Keep selection pure. Move grouping or building objects into a named selector or an upstream node.

Call a compiled graph with `.Graph("child", childGraph)`. Its input type must match the current value. Use a named `Then` for any conversion before the call. `SkipIf` can follow the call and mark the child as skipped based on its result, which is still passed downstream.

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

The run reaches `AwaitingReview` and the process may exit. The Heron repository includes a complete example at `Examples/Pregel/review/Review.csx`.

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

Once the body has started, success, failure, cancellation and early return all enter `Finally`. A pending review pauses the run before cleanup. Cleanup receives a `PregelOutcome` with the original input and the body status. Check `HasValue` before using the committed final value. Cleanup must tolerate running twice because external effects and checkpoints are separate operations. Only independent cleanup nodes may use `ContinueOnError`.

## Templates

A plain graph already gets a form and run history. Add a template for dynamic choices, validation, parameter previews or result summaries. Override `CreateGraph` to return the compiled graph. Use `AutomationGraphTemplate<TInput, TOutput>` and override `Complete` to summarize a typed result. Use `AutomationGraphTemplate<TInput>` and override `SuccessSummary` when the graph returns the input type and the host should only report a summary.
