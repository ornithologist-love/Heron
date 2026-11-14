---
type: architecture
covers: [Source/Client.Controls/**, Source/Client.Controls.Gallery/**, Source/Client.Controls.Gallery.Plugin/**]
---

# ClientControls 与控件 Gallery

`Heron.ClientControls` 拥有通用 Avalonia 控件、颜色 Token、语义样式和自动化界面的通用布局样式。它不引用产品宿主、业务插件、自动化引擎或服务器。品牌标识、第三方应用或业务工具图标、业务激活、持久化、导航和桌面通知服务属于消费方，不进入控件包。

源码沿用 `Source/Client.Controls`，程序集、命名空间和 NuGet 包统一为 `Heron.ClientControls`。主资源入口为 `Themes/Controls.axaml`，语义样式入口为 `Themes/HeronTheme.axaml`。颜色 Token 与样式保留原有语义键，使用动态资源同时支持明暗主题，消费方不复制一套主题正文。

## 窗口外壳控件

`Chrome/` 下是宿主窗口外壳用的通用件：标题栏按钮（胶囊与仅图标两种，标题栏内按下不触发窗口拖动）、三态主题菜单、头像（图片或首字母）、弹出卡片、菜单卡片项和带呼吸光点的状态徽标，样式在 `Themes/Chrome.axaml`，随 `HeronTheme.axaml` 一起生效。它们只负责外观和交互，文案由使用方给：主题菜单默认是"跟随系统 / 浅色 / 深色"，宿主可以改名。菜单用原生弹出层承载自绘卡片，而不是 Flyout，因为 Flyout 的呈现器会在圆角外漏出底色；主题因此把 `PopupRoot` 设为透明。组装这些控件的窗口是框架层的 [`ShellWindow`](client-framework.md#shell-window)。

三态主题菜单的选项须在呈现器创建前初始化，每次打开时再更新宿主文案和当前勾选。只在 `Opening` 中首次填充选项，会使 Avalonia 的菜单呈现器绑定到初始空集合，出现菜单已弹出却没有内容的情况。

## Gallery 边界

`Source/Client.Controls.Gallery` 是独立可执行的控件工作台，只引用控件项目和公共 UI 依赖，不引用消费方源码或 SDK。组件按两级 Tab 组织，切换时只创建当前展示页；Toast 示例在页面离开时清理通知。独立窗口各自创建控件实例。

唯一的业务布局示例是自动化 Mock，数据与操作全部留在内存。它不加载流程、参数编辑器、插件、真实业务 View 或自动化引擎。其余页面只展示颜色、输入、表格、按钮、Markdown、Toast、模态框和 Popup。

在本仓启动：

```powershell
dotnet run --project Source/Client.Controls.Gallery --configuration Debug
```

Gallery 输出至工作区 `Binaries/Heron/Gallery/`。同一套页面还编译成插件 `heron.gallery`（`Source/Client.Controls.Gallery.Plugin`），输出到 `Binaries/Heron/Plugins/heron.gallery/<版本>/`，宿主在版型里点名即可把 Gallery 作为一个页面挂进来；作为页面时主题跟随宿主，不显示自带的主题切换。插件版本只链接页面源码，不带独立程序的桌面运行时和热更新。写法见[客户端插件](client-plugins.md)。Debug 支持原生 AXAML 热更新，Release 不引用 HotAvalonia 或运行时 XAML Loader。Gallery 只验证控件呈现和交互，消费方业务的成功、取消、下载与远程生命周期仍由消费方验证。

## 包与源码联调

打包与交付遵循[包交付流程](package-workflow.md)。控件包携带必要主题、控件和第三方声明，Gallery 不随库打包。使用包的消费者不需要检出 Heron 源码；需要联调时由消费者显式选择源码引用，不根据目录是否存在自动切换。

共享控件程序集必须由宿主和插件使用同一份。消费方更换程序集名时同步调整资源 URI、共享名单、SDK 导出和构建输入指纹，不通过宽泛的程序集名称前缀放开所有框架包。程序集版本固定不能替代类型名和程序集名的兼容处理。
