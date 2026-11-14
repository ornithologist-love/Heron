---
type: architecture
covers: [Source/Pregel/**, Source/Pregel.Tooling/**, Source/Pregel.Scripting/**, Source/Pregel.Automation/**, Source/Pregel.Cli/**]
---

# Heron Pregel

Heron.Pregel 是独立的 .NET 图框架，拥有内核与 DSL、外部进程适配、CSX 装载、本地任务和独立 CLI。包、程序集和公开命名空间统一使用 Heron.Pregel 及相应后缀。框架只依赖 .NET 和公共包，不依赖产品源码、界面框架或私有服务。引擎设计见[引擎正文](pregel-engine.md)，用法见[编写范式](pregel-authoring.md)。

## 身份与宿主边界

AssemblyVersion 固定为 1.0.0.0，但程序集改名与命名空间改名仍会改变类型身份。图指纹、节点 ID 和编码器可能依赖这些身份，因此不能把改名描述为没有兼容风险。

内核提供持久化名称映射，脚本层提供语法树级的名称转换。嵌入宿主在构图和装载脚本前登记自己的旧名称；用户源文件、字符串值、注释和内容 revision 不被重写。旧二进制插件需要按新程序集重新编译，源码转换不替代二进制兼容。

自动化宿主通过运行时接缝提供数据目录、脚本源、执行快照、计划归属与环境作用域。组织账号、发布审核、远程机器通信、AI 和通知渠道留在消费方，框架不包含产品专用的旧名称映射。

脚本所在的包目录与执行快照不是可写位置。每轮运行的产物写节点上下文的 `WorkingDirectory`，持久缓存（编译出的工具等）通过 `AutomationScriptStorage.CacheDirectory` 取得：宿主可把已安装或已准入的脚本映射到包外的缓存目录，没有映射的本地脚本使用自身被排除在内容哈希之外的 `.cache`。宿主还可为步骤错误追加说明，例如把写包目录导致的拒绝访问指向正确位置；一次运行开始时通知宿主它使用的快照，供保留策略按最近使用计算。

远程派发与资源传输由 [AutomationTenants](automation-tenants.md) 组合本地宿主；每个宿主独立提供外部日历归属。存储互斥身份由嵌入方配置，使同一数据目录中的新旧执行进程继续使用同一个锁；产品身份和旧任务格式升级仍由嵌入方维护。

独立 CLI 命令为 heron-pregel，其构建输出位于工作区 Binaries/Heron/Pregel.Cli，也可按 .NET tool 安装。HERON_PREGEL_HOME 选择数据根，默认使用用户目录下的 .heron-pregel；框架直接使用 Heron.Pregel 身份，不登记前产品名称的兼容别名。

## 构建与验证边界

按[包流程](package-workflow.md)构建并向消费方交付。Debug 保留物理源码和独立 PDB，支持源码调试；Release 将映射后的符号嵌入程序集，避免程序集重写再次记录本机 PDB 文件路径。ClientControls 的 Debug HMR 门控保持独立，Release 不启用 HMR。

内核测试随内核维护在 Tests/Heron.Pregel.Tests，消费方自己的集成测试由消费方维护。框架测试使用隔离的 mock 数据验证不变量、契约与安全属性，并通过 Anchor 标记关联对应的成文事实；业务流程与真实数据留在消费方。
