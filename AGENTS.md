# Heron 开发指南

- 保持本仓的命名、分层、开发和文档规范；源码使用 `Source`，生成交付物使用 `Binaries`。
- 修改构建或交付流程前先读 [package workflow](Docs/package-workflow.md)；源码使用 `Source`，新增目录按已有约定命名。
- 控件、主题与 Gallery 先读 [ClientControls](Docs/client-controls.md)；Gallery 使用 `dotnet run --project Source/Client.Controls.Gallery --configuration Debug`，输出到工作区 `Binaries`；消费方的标准开发入口继续有效。
- Pregel 内核、脚本层、独立自动化和 CLI 先读 [Pregel](Docs/pregel.md) 与 [引擎设计](Docs/pregel-engine.md)；继续使用标准 `Pack.ps1` 构建，产物位于工作区 `Binaries/Heron`。
- 自动化原生页面、宿主贡献面板与独立启动先读 [Pregel Avalonia](Docs/pregel-avalonia.md)；页面生命周期不拥有后台执行。
- 本仓只放可复用框架代码，严禁引用消费方源码树、具体产品宿主、私有服务或业务插件。
- 写或装配客户端插件先读[客户端插件](Docs/client-plugins.md)；独立启动器 `dotnet run --project Source/Client.Host -- --plugins <dir> --smoke` 不依赖任何产品宿主。
- ClientFramework 的插件模型、桌面契约与运行时先读 [ClientFramework](Docs/client-framework.md)；宿主接口版本、业务文案、窗口策略及原生文件过滤规则由消费方维护。
- 远程 Worker、派发协议、子图传输与资源服务先读 [AutomationTenants](Docs/automation-tenants.md)；身份验证和产品接线由消费方提供。
- 框架测试要写：新增或迁入的框架能力为其不变量、契约与安全属性补测试，每个测试标 `[Anchor]` 指向 `Docs/` 中成文的事实；已有测试随所属库迁移。不收业务测试（产品文案、版型、业务流程留在消费方仓）。不要为了让测试通过改断言或改行为。
- 禁止扩大任务范围或创建隔离编译目录；标准打包入口为 `pwsh -File ./Pack.ps1`（在项目正常的 `bin` / `obj` 目录编译，`-List` 仅评估包元数据不编译）。
- Debug 模式保留物理源码路径以支持源码调试与 XAML 热重载；Release 包使用共享 PathMap 与嵌入式符号。
- 宿主改名需在嵌入方显式执行身份与脚本迁移，禁止把产品的旧品牌名称硬编码进框架；`AssemblyVersion` 应独立于包版本保持稳定。
- 未经用户明确要求，禁止推送、部署、发布到远端源或创建发布 tag；本地打包不需要 commit 或 tag。
- 过程记录放已忽略的 `.work/`；长期文档放 `Docs/` 并携带治理元信息头。
- Go 租户控制服务先读 [AutomationTenants server](Docs/automation-tenants-server.md) 与模块 AGENTS；构建入口 `pwsh -File ./Pack.ps1 -ServerOnly`，Go 测试遵守模块中的框架契约锚点规则。
