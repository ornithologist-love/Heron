---
type: guide
covers: [Source/Pregel/Dsl/**, Source/Pregel/PregelGraph.cs, Source/Pregel.Scripting/PregelScript.cs]
---

# Pregel 编写范式

图定义、输入模型和节点行为分开：先定义明确的数据契约，在节点中执行外部操作，构图阶段只声明依赖和控制流。对可信 CSX 的装载会执行顶层代码，因此校验和描述也不应承载业务副作用。

外部操作须考虑失败后的重复调用；阶段不是事务，取消和 Finally 不会撤销已经完成的文件或网络操作。

入口使用 `Pregel.Define<TInput>(id)`，无输入图用 `Pregel.Define(id)`。节点使用 `.Then(Node)` 串接。图 ID 由作者指定，脚本版本使用内容哈希。

`Then(Node)` 使用具名方法，统一接收 `(current, context, token)`。返回 `void` 或 `Task` 时，节点执行后继续传当前值。返回 `T` 或 `Task<T>` 时，下游接收该结果。下游需要共享的数据放进可序列化的当前值。节点可以提交有明确类型的空值，恢复时从检查点读取。

- `When` 判断前一个节点的输入。`Retry` 设置该节点的重试策略，`ContinueOnError` 允许失败后继续。同类型节点跳过或失败后继续时，保留先前已提交的值。换类型节点使用 `When` 或 `ContinueOnError` 会在构图时报错。
- `ReturnIf` 判断前一个节点执行后的值。每个提前结束点都必须能提供图的最终结果，否则编译时报错。

## 编译与运行

`Compile()` 返回 `PregelGraph<TInput, TOutput>`。宿主用 `graph.Prepare(input, options)` 创建运行，或用 `graph.RunAsync(input, options, token)` 直接执行。通过同一个 `PregelRunOptions` 提供 `Engine` 和 `Observer`。默认使用内存引擎，需要跨进程恢复时由宿主提供持久化引擎。执行成功后用 `graph.Read(result)` 读取类型化结果。

CSX 入口返回编译好的图或自动化模板，由宿主执行。

## 阶段与收尾

- `Stage(id, title, body, when)` 的条件读取阶段入口值。条件阶段必须以相同类型结束。阶段内部可以经过其他类型，例如 `A → B → A`。阶段跳过时，下游仍收到阶段的入口值。
- `Finally(cleanup)` 在正文完成、失败或取消后收到 `PregelOutcome`。其中 `Input` 是原始输入，`Status` 和 `Error` 来自正文。`HasValue` 表示最终类型的值已提交，提交的 `null` 也算有值。需要释放中途资源的节点应在负责该资源的图内完成收尾。

用 `Graph(id, graph)` 将当前值交给已编译的子图，并把子图结果传给下游。可用 `target: value => value.Machine` 指定机器。`SkipIf` 只能紧跟 `Graph`，按子图结果标记跳过，同时保留该结果。

## 并行与按集合展开

分支写成 `x => x.Then(...)`。固定分支用 `Parallel(a => …, b => …)`，每条分支从独立的入口值开始，汇合后继续使用分叉前的值。

- `ForEach` 对每个选中项执行分支，汇合后沿用进入展开前的值。需要收集结果时用 `Map`。它返回 `PregelMapResult`，`State` 是进入展开前的值，`Items` 按选择顺序排列。空集合也保留 `State`。两者默认串行，需要并行时指定 `mode: PregelMutationMode.Parallel`。
- 分支开头的 `When` 管整条分支：不满足时分支内的节点都记 `Skipped`，分支仍参与汇合。写在 `Then` 后面的 `When` 只管那一步。
- 集合分支为每个选中项分配稳定的实例身份，实际执行的子运行有自己的检查点。`Where` 在展开前筛掉条目。分支开头的 `When` 为假时，展开计划记录原因，实例显示为 `Skipped`，汇总结果列表只包含实际执行过的项。`SkipIf` 根据执行结果标记跳过，并将这条业务结果交给汇总。
- `items` 只取一个集合，最多加一个 `Where`。分组和拼装对象放进有名字的方法或上游节点。纯内存选择只在展开时执行一次，结果随展开计划提交，恢复时复用该计划。
- `key` 是每一项的稳定业务身份。展开 ID 和 `key` 共同决定子运行身份。
- 分支只用默认参数调用一张编译好的图时，直接以那张图展开；分支含其他步骤时，在 `Compile` 时编成一张模板图。

独立 CLI 的具体编写流程与示例由[Agent 编写指南](../Skills/heron-pregel/references/authoring.md)维护；命令生命周期见[操作指南](../Skills/heron-pregel/references/operations.md)。公开 API 从当前版本导出，避免维护手写的重载副本。
