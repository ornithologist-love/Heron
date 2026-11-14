---
type: guide
covers: [Source/Pregel/PregelDsl.cs, Source/Pregel.Scripting/PregelScript.cs]
---

# Pregel 编写范式

图定义、输入模型和节点行为分开：先定义明确的数据契约，在节点中执行外部操作，构图阶段只声明依赖和控制流。对可信 CSX 的装载会执行顶层代码，因此校验和描述也不应承载业务副作用。

图结构、条件或类型契约变化时更新 revision，不用旧检查点解释新语义。外部操作须考虑失败后的重复调用；阶段不是事务，取消和 Finally 不会撤销已经完成的文件或网络操作。

独立 CLI 的具体编写流程与示例由[Agent 编写指南](../Skills/heron-pregel/references/authoring.md)维护；命令生命周期见[操作指南](../Skills/heron-pregel/references/operations.md)。公开 API 从当前版本导出，避免维护手写的重载副本。
