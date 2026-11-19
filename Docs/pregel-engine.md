---
type: architecture
covers: [Source/Pregel/**, Source/Pregel.Tooling/**]
---

# Pregel 图执行引擎

> 引擎内部实现的设计与现状。编写范式（怎么用）见 [Pregel 编写范式](pregel-authoring.md)，改图代码前先读那篇。

## 概述与边界

`Heron.Pregel` 是可独立构造、执行、测试和恢复的 C# Pregel Core，负责一张 Graph 的一次 Run：根据 Graph 和 Checkpoint 计算可运行的 Invocation，执行节点，持久化 Pending Writes，在同步屏障提交状态并继续，直到完成、失败、中断或取消。

**纯净边界**：Core 只拥有 Graph、Stage、Node、Edge、Channel、Reducer、Invocation、Attempt、Superstep、Barrier、Trigger、Send、Mutation、ExpansionPlan、Pending Writes、Retry、Interrupt、Resume、Cancellation、Checkpoint 这些概念。不拥有外部配置文件格式、流程插件、反射注册、任何业务对象、Secret、Client UI、进程执行（后者属于单向依赖 Kernel 的 `Heron.Pregel.Tooling` 适配层）。依赖方向只能是宿主依赖 Core，Core 不引用任何宿主项目。

## 为什么是 Pregel、为什么用 C# 重写

长时间运行的自动化通常包含条件分支、并行、重试、动态展开和人工审核。引擎将执行状态与宿主生命周期分开，避免恢复逻辑散落在脚本和服务回调中。

Pregel 的节点、通道和同步屏障让并发写入有确定的提交边界。强类型 C# 接口在构图与编译阶段检查数据契约；检查点保留执行身份，进程退出后可以继续。业务配置、界面和远程传输由单向依赖内核的适配层负责，不反向进入内核。

## 核心设计规则

1. **编译定义不可变**：编译时验证 ID 唯一、Edge/Channel/Trigger 引用存在、类型相容，并绑定 mutation 子图模板和展开规则；每次 Run 的拓扑由编译定义加已提交展开计划得到，不修改共享的编译对象。Edge 只表示方向，条件路由由 Trigger、Channel write、`Command.Goto` 或 `Send` 表达。
2. **Channel 契约驱动**：每个 Channel 定义 `ApplyWrites(current, ordered) -> next`；Kernel 不推断业务含义。为保证恢复与重放一致，按稳定 Invocation path 和 write index 排序；只有值实际变化的 Channel 递增版本；Trigger 读版本，不读墙钟。
3. **Superstep 屏障语义**：同批 Invocation 读同一份已提交 Checkpoint；只产生局部 Writes；先持久化 Outcome，必需 Invocation 全部成功、跳过或以 ContinueOnError 允许失败后才提交 Barrier；未提交的下一轮观察不到 Pending Writes。允许继续的失败只触发声明的后续边，不提交失败写入和流程命令。
4. **确定性 identity**：`LogicalInvocationId` 由稳定 path/Node/Send index 的 hash 确定（非随机 GUID），跨 Retry/Resume 稳定；`AttemptId` 每次尝试唯一。成功 Outcome 先持久化才参与 Barrier；`PutOutcome` 幂等，`CommitBarrier` 以基准 Checkpoint version 做 CAS，不出现半提交。
5. **结构化 Outcome**：节点异常在 Invocation 边界转为 `Succeeded/Failed/Interrupted/Cancelled`，以 LogicalInvocationId 为幂等键持久化；存储自身失败属于基础设施失败，Kernel 停止推进。
6. **Interrupt/Resume**：在确定位置暂停并持久化 Checkpoint + 未消费 Pending Writes；Resume 强制 fingerprint 一致，已成功持久化的 Invocation 不重复执行。外部信号由宿主提交给公开入口，Core 不轮询外部系统。
7. **终止状态只有四个**：`Completed / Failed / Interrupted / Cancelled`。宿主进程退出不自动记 Failed；无持久化终态则下次按 Checkpoint 恢复。必须有 Superstep/递归上限，超限产生结构化错误。
8. **错误必须带身份**：GraphValidation/NodeExecution/RetryExhausted/ChannelUpdate/CheckpointConflict/Persistence/Serialization/RecursionLimit 八类，附带 RunId、CheckpointId、Superstep、NodeId、LogicalInvocationId、AttemptId 中当前可用字段。

## 实现现状

源码按职责位于 Source/Pregel、Source/Pregel.Tooling、Source/Pregel.Scripting、Source/Pregel.Automation 与 Source/Pregel.Cli。内核只依赖 BCL；进程执行和脚本编译依次在外层接入。本地自动化和 CLI 复用内核，不引入图形界面或产品宿主依赖。

关键方案：

- 公开 API 用 C# 强类型（`ChannelRef<TState,TUpdate>`、`NodeRef<TInput>`、`ICheckpointCodec<T>`），不定义统一 JsonElement 值模型；类型错误编译期失败。Kernel 内用非公开 `IChannelRuntime`/`INodeRuntime` 类型擦除。
- `Fingerprint` 只由规范化结构字段计算，不含显示文本/Delegate/输入/时间戳；Resume 强制匹配。仅使用原有 `When` 条件、无显式路由/路由作用域/Mutation 的图，还会重算条件改为 Continue/Skip 实现前的等价指纹，以恢复旧检查点；节点、通道、边、Finally、Mutex 和 revision 仍全部参与匹配，检查点不改写为新指纹。
- Invocation identity：`PULL = Hash(RunId, Superstep, "pull", NodeId, sorted TriggerIds)`；`PUSH = Hash(RunId, Superstep, "push", SendIndex, TargetNodeId, ParentPath)`。
- Planner 是纯函数：PULL 由 Channel 版本 > Node 已见版本产生，PUSH 由已提交 Send packet 产生；已有成功 Outcome 的 LogicalInvocation 进 replay 集合不重跑。
- 文件 Store 用 append-only + commit manifest 协议（`runs/{run-id}/{run.json, checkpoints/, pending/, commits/}`），manifest 是提交完成的唯一事实。
- 图变量（2026-09-24）：声明后每次 Run 由调用方提供值，存放在保留的 `__var:{name}` LastValue channel 随 Checkpoint 持久化；Fingerprint 只含声明不含 Run 值。缺变量报 `missing_graph_variable`，传未声明变量报 `unknown_graph_variable`。
- 顺序阶段：GraphDefinition 保存 Stage 身份、成员和入口；入口将阶段条件的结果写入 checkpoint。执行器先检查阶段条件，再检查节点自己的条件，整段跳过仍产生各成员的 `Skipped` outcome 并沿原边推进。阶段不提供事务回滚或额外并发语义。
- 节点流程选择统一为 Route：编译定义保存执行前/后的候选决策，绑定独立于节点主体；执行器校验选择结果并落为原有控制边写入或 End。When、Choose、FastReturn 只负责声明这套规则，不各自执行流程控制。候选目标在编译时封闭并参与指纹；结果路由只读取本 invocation 刚产出的结果，不放宽普通节点的 checkpoint 读取规则。已持久化 outcome 包含所选边的写入，恢复重放 outcome，不重复判断已完成的路由。
- 高层具名 Route 将选择写入每次运行的 checkpoint，分支成员保存指向该选择的 scope。业务节点在执行前按外层到内层检查 scope，未选中的分支产生原生 Skipped outcome；屏障控制节点继续推进，汇合使用 NamedBarrier，避免跳过分支先到而提前推进。scope 与候选 key 参与指纹并随 mutation 运行图投影保留；Stage 的条件也覆盖隐藏的路由判断节点，使整段跳过时不执行 selector。复用现有节点、屏障和 outcome，不引入第二套分支状态记录。
- ContinueOnError：普通节点与 Finally 均可显式允许失败后继续，重试策略先执行完，durable outcome 和事件仍保留 Failed。正文错误随 `Checkpoint.ContinuedErrors` 保存，恢复沿后续边推进；Finally 错误保存在 `Finalization.Errors`，仍需显式重试收尾。取消、中断、屏障/存储错误不受此选项豁免。

## Mutation：预编译规则与运行拓扑

`PregelMutationDsl` 将规则注册到 `CompiledPregelGraph.Mutations`：规则 ID、串行或并行模式、入口、汇合点、输入输出 codec 和已编译子图。父图指纹包含这些结构字段、子图指纹及规则格式版本。第一版只支持实例化已编译子图，不提供任意替换或删除正在执行节点的接口。

集合分支（`Parallel`、`ForEach` 的 `id, items, key, branch` 写法）编译成同一种 mutation 规则：`items` 与 `key` 合成带 Key 的条目，分支 lambda 在构图时编成独立的模板图，图 ID 为父图 ID 加展开 ID，revision 随父图。模板只含一次从分支起点发起、不带条件的 `Graph` 调用时，直接以被调用的图作模板，规则、模板指纹与等价的旧 `Mutate` 相同，已有检查点可以继续恢复。`SkipIf` 写入该调用的结果跳过规则，展平时一并带到展开规则上。固定分支的 `Parallel` 由同一图内的命名屏障汇合。

分支开头的 `When` 生成一个隐藏的判断节点，把结果写进本次运行的选择通道。分支内的其余节点挂上与具名 Route 相同的 scope，判断不通过时产生原生 `Skipped`，屏障到达节点照常推进。

展开入口从已提交上游状态读取带 Key 的集合，编码每份输入并产生 `Mutate` 命令。Pending Outcome 保存这份计划；屏障检查实例数量、Key 和来源，原子提交 `Checkpoint.Mutations`、`RunGraphRevision`、新增通道初值与首批调度包。提交成功才发布 `mutation.committed`。崩溃恢复从检查点重建相同实例和边，不重新选择条目；不兼容的计划格式或模板指纹明确拒绝恢复。

`PregelMutationGraph` 把计划投影成父运行自己的实例节点和控制边：串行链或并行分叉/汇合。节点有稳定 ID、标题及子图定义，阶段成员随拓扑更新。原始编译对象和其他运行实例保持独立。完成的计划仍留在该运行中供历史展示；同一规则最多存在一份未完成计划。

实例节点由 `PregelEngine` 直接执行，使用同一检查点 Store 和确定的 ChildRunId 创建或恢复原生子运行。子图保留自己的 channel、superstep、重试、资源准入和 Finally；不会复制含类型引用的 channel runtime 到父图。子运行已完成、父结果尚未提交时，恢复读取子检查点，不再执行子图正文。普通业务副作用仍需遵守既有幂等约定，mutation 不保证外部操作恰好执行一次。

每份成功结果通过 `MutationResult` 命令随父屏障提交；汇合节点按计划顺序收集并标记完成。并行实例处于同一父 superstep，屏障等待全部结果，不因某个实例先结束而提前执行汇合。每张运行的累计实例数受 `MaxMutationInstances` 限制。

`PregelMutationSnapshot` 序列化运行拓扑，`GetRunGraphAsync` 提供只读查询。Client 的拓扑投影和运行记录器消费快照并合并已有状态；业务模板不管理展开数量和实例身份。

CSX 的 Roslyn 临时程序集名包含随机会话 ID。类型身份与默认 JSON codec 身份会移除这一临时部分（包括泛型参数中的部分），防止同一源码重新加载后图指纹漂移。节点方法签名使用同样的规范化。旧 CSX 检查点不强制迁移；行为变更仍由图 revision 区分。

## 手动继续与从节点重新运行

`PregelEngine.CreateReexecutionAsync` 从终态运行的已提交检查点创建新运行，不修改来源的终态、日志与结果。`Continue` 只接受失败或取消的正文；`FromNode` 接受已调度节点的地址（逐层 mutation 实例 ID，最后是节点 ID）。输入与状态采用原检查点的编码值，指纹必须一致。新检查点记录来源运行、来源检查点、模式、节点地址及复用节点；普通 `Resume` 的原有终态语义不变。`PreviewReexecutionAsync` 按同一套规则校验并返回恢复点、继承节点和同批复用节点，只读历史、不创建运行；`CreateReexecutionAsync` 拒绝的请求，预检同样拒绝，宿主据此在创建记录前说明原因。

MVP 的恢复边界是屏障：继续选择最后一个正文检查点；指定节点选择调度该节点之前的检查点。复用之前已提交的状态，并把同批其他成功或跳过的 outcome 重新绑定到新运行的 invocation；选中节点及此后屏障重新执行。后续屏障中的独立工作也可能重新执行，不提供“只执行单点”或任意依赖切片。Reducer、版本、控制通道、Send 与展开计划整体来自同一检查点，不能从最终状态扣掉几笔写入。

文件存储只沿 manifest 的父链读取历史，忽略未提交的孤立文件。每次屏障把所消费的 outcome 随新检查点保留；pending 仍按原规则清理。派生运行的首批复用 outcome 随初始检查点一起持久化，避免创建后崩溃丢失复用依据。`node.reused` 只为继承节点和这批来源种子发布；运行自身已持久化的 outcome 在崩溃恢复中重放属于普通恢复，不报告为复用。旧存储未保留 outcome 时，继承节点的结果记为 `PregelReusedNode.CommittedWithoutOutcome`，由宿主按原记录投影。旧运行缺少已消费 outcome 时不能据此猜造结果，同批节点可能重新执行。派生历史只包含派生点之后的检查点；更早的节点须在来源运行选择。

持久化 mutation 逐层恢复：已成功的同批实例只读复用结果；失败或取消的子运行另建派生检查点；指定子节点时逐层回退其父调用屏障；后续串行实例重新开始。完整子树校验后才创建检查点，父运行最后发布；创建失败留下的子检查点不执行。新展开的子运行继承派生来源。共享原工作数据的派生运行通过来源资源锁串行准入。

安全边界：待审核、尚未结束收尾的运行保留原处理入口；图含 Finally/Recovery 时，中途恢复因资源可能已被释放而拒绝，只允许从入口重新开始。循环节点涉及多轮时拒绝含糊的节点地址。普通委托自行启动的非原生子图不支持内部定位。检查点不克隆外部文件、进程或远端副作用；节点仍须遵守持久输入与幂等契约，缺失工作文件直接报错。Tooling 的归档节点在派生运行中使用新运行目录，保留原归档。

## 手动跳过节点与 Stage

用户只想跑图的一部分时，宿主在 `PregelRunOptions.ManualSkip` 里列出本次跳过的根图节点 ID 与 Stage ID。被选中的节点不调用正文，以原生 `Skipped` 完成并沿普通出边推进；跳过 Stage 等同其条件为假，覆盖成员与其中的路由判断节点，Stage 入口仍执行。屏障到达与汇合节点不能点名，所以跳过 Parallel 或 Route 里的分支后，屏障照常满足。跳过的节点不写状态，后继读到的是此前已提交的值或初值；是否只跳过彼此独立的部分由使用者判断，内核不推断依赖。

这份选择属于一次调用，不进图指纹、不进检查点：恢复同一运行时宿主须传入同一份选择，否则未执行的节点会照常运行。mutation 子运行不继承选择，节点 ID 只在根图里解析；未知节点、未定义的 Stage 或屏障控制节点在启动前以 `invalid_manual_skip` 拒绝。内核不知道“手动”来自界面还是 CLI，跳过原因由调用方给出。

## 核心不变量（评审清单）

1. Core 是通用 Graph 执行引擎，不含业务模型。
2. `Send` 只产生 Invocation，不改 Graph 拓扑。
3. 当前 Superstep 未提交的 Writes 对下一轮不可见。
4. 单 Node 失败不丢失其他已成功 Invocation 的 Pending Writes。
5. 规定崩溃点退出后能恢复，不重复已提交计算。
6. 相同 Graph、Checkpoint、输入必须产生相同下一步执行计划。

## 延后项

用户工作流语言/YAML Schema、插件发现/版本/部署/权限、定时与资源调度、分布式运行、Secret/凭据、UI/运行历史。只能经公开 Graph、Node Handler、Checkpoint Store、执行入口接入，不能提前改变 Kernel 类型。
