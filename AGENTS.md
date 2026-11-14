# Heron development

- 保持本仓的命名、分层、开发和文档规范；源码使用 Source，生成交付物使用 Binaries。
- 目录与交付约定见 [package workflow](Docs/package-workflow.md)；源码使用 `Source`，新增目录按已有约定命名。
- Read [package workflow](Docs/package-workflow.md) before changing build or delivery.
- 控件、主题与 Gallery 先读 [ClientControls](Docs/client-controls.md)；Gallery 使用 `dotnet run --project Source/Client.Controls.Gallery --configuration Debug`，输出到工作区 Binaries；消费方的标准开发入口继续有效。
- Pregel 内核、脚本层、独立自动化和 CLI 先读 [Pregel](Docs/pregel.md) 与 [引擎设计](Docs/pregel-engine.md)；继续使用标准 Pack.ps1 构建，产物位于工作区 Binaries/Heron。
- Reusable framework code belongs here. Do not reference consumer source trees, product hosts, private services or business plugins.
- 写或装配客户端插件先读[客户端插件](Docs/client-plugins.md)；独立启动器 `dotnet run --project Source/Client.Host -- --plugins <dir> --smoke` 不依赖任何产品宿主。
- ClientFramework 的插件模型、桌面契约与运行时先读 [ClientFramework](Docs/client-framework.md)；宿主接口版本、业务文案、窗口策略及原生文件过滤规则由消费方维护。
- 远程 Worker、派发协议、子图传输与资源服务先读 [AutomationTenants](Docs/automation-tenants.md)；身份验证和产品接线由消费方提供。
- 框架测试要写：新增或迁入的框架能力为其不变量、契约与安全属性补测试，每个测试标 `[Anchor]` 指向 `Docs/` 中成文的事实；已有测试随所属库迁移。不收业务测试（产品文案、版型、业务流程留在消费方仓）。不要为了让测试通过改断言或改行为。Do not expand scope, or create isolated compilation directories. The standard packaging entry is `pwsh -File ./Pack.ps1`; compilation stays in the projects' normal `bin` and `obj` directories. `-List` evaluates package metadata without compiling.
- Preserve physical source paths in Debug for source debugging and XAML hot reload. Release packages use the shared PathMap and embedded symbols.
- Renames require explicit identity and script migrations in the embedding host; do not hardcode a product's former names into the framework. Keep AssemblyVersion stable independently of package versions.
- Do not push, deploy, publish to a remote feed or create release tags unless explicitly requested. Local package creation does not require a commit or a tag.
- Keep process notes in ignored `.work/`; durable documentation lives in `Docs/` with governance metadata.
- Go 租户控制服务先读 [AutomationTenants server](Docs/automation-tenants-server.md) 与模块 AGENTS；构建入口 `pwsh -File ./Pack.ps1 -ServerOnly`，Go 测试遵守模块中的框架契约锚点规则。
