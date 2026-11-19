---
type: guide
covers: [Source/Pregel/PregelDsl.cs, Source/Pregel/PregelCollectionDsl.cs, Source/Pregel.Scripting/PregelScript.cs]
---

# Pregel 编写范式

图定义、输入模型和节点行为分开：先定义明确的数据契约，在节点中执行外部操作，构图阶段只声明依赖和控制流。对可信 CSX 的装载会执行顶层代码，因此校验和描述也不应承载业务副作用。

图结构、条件或类型契约变化时更新 revision，不用旧检查点解释新语义。外部操作须考虑失败后的重复调用；阶段不是事务，取消和 Finally 不会撤销已经完成的文件或网络操作。

用 `Graph(id, graph)` 调用已经编译的图。当前链的值按其类型直接传给子图；需要转换时，在前一步用 `Then`。子图有结果时以该结果继续，无结果时继续使用原值。可用 `target: value => value.Machine` 从输入选择机器。

## 并行与按集合展开

几段可以同时跑或依次跑的工作，都写成分支 `x => x.Then(...)`。写法只取决于分支是写死在图里，还是来自运行时的集合：

| | 分支写死在图里 | 分支来自运行时集合 |
|---|---|---|
| 同时跑 | `Parallel(a => …, b => …)` | `Parallel(id, items, key, branch)` |
| 依次跑 | `.Then(…).Then(…)` | `ForEach(id, items, key, branch)` |

- 分支开头的 `When` 管整条分支：不满足时分支内的节点都记 `Skipped`，分支仍参与汇合。写在 `Then` 后面的 `When` 只管那一步。
- 集合分支为每个选中项分配稳定的实例身份，实际执行的子运行有自己的检查点。`Where` 在展开前筛掉条目。分支开头的 `When` 为假时，展开计划记录原因，实例显示为 `Skipped`，汇总结果列表只包含实际执行过的项。`SkipIf` 根据执行结果标记跳过，并将这条业务结果交给汇总。
- `items` 只取一个集合，最多加一个 `Where`。分组、拼装新对象、读取 State 等计算放进有名字的方法或上游节点，图上只写方法名。它只做纯内存计算：在展开时执行一次，结果随展开计划提交，恢复时不会重跑。
- `key` 是每一项的稳定业务身份，恢复时靠它认出同一项，不用序号或随机值。展开 ID 和 `key` 共同决定子运行身份，所以只有集合分支需要它们。`title` 和 `target` 可选。
- 分支只用默认参数调用一张编译好的图时，直接以那张图展开；分支含其他步骤时，在 `Compile` 时编成一张模板图。
- `Graph(...).SkipIf(result => reason)` 在结果给出原因时，把这次调用记为 `Skipped`；返回 null 或空字符串表示正常完成。它只能紧跟在返回结果的 `Graph` 后面。

独立 CLI 的具体编写流程与示例由[Agent 编写指南](../Skills/heron-pregel/references/authoring.md)维护；命令生命周期见[操作指南](../Skills/heron-pregel/references/operations.md)。公开 API 从当前版本导出，避免维护手写的重载副本。
