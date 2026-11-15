---
type: guide
covers: [Source/Client.Controls.Gallery.Plugin/**, Source/Client.Host/**, Source/ClientFramework.Abstractions/IClientPluginModule.cs]
---

# 客户端插件：规则、写法与装配

一个客户端插件是一个**包目录**：`plugin.json` 加插件程序集和它私有的依赖。宿主不认识插件源码，也不扫描目录；它只装配版型（Edition）点名的包。本篇以 `Source/Client.Controls.Gallery.Plugin`（把控件 Gallery 变成一个页面）为例，说明插件要守的规则、最少要写的代码，以及怎么把它装进宿主。机制本身的正文在 [ClientFramework 插件模型](client-framework.md)。

## 规则

1. **只引用框架契约。** 插件只能编译依赖 `Heron.ClientFramework.Abstractions`（模块、页面、命令、状态）以及宿主在 `host-api.json` 里声明共享的程序集（框架、`Heron.ClientControls`、Avalonia 等）。不得引用宿主可执行文件或宿主源码。
2. **插件之间不互相引用。** 需要协作时，由提供方用 `context.Export<T>` 导出一个共享契约里的接口，使用方在每次操作时用 `ImportOptional<T>(pluginId)` 取用；对方缺席时功能降级，不报错。
3. **共享的不打包，私有的自己带。** 共享程序集用宿主那一份，以 `Private="false"` 或 `ExcludeAssets="runtime"` 引用，包里不放副本；身份对不上时装配直接失败，不会悄悄带一份私有拷贝过去。其余依赖放在包里，在插件自己的加载上下文中隔离。插件带了比运行时更新的框架库（例如 8.0 运行时上的 9.0 包）时，用插件自带的那份。
4. **`host_api` 必须与宿主一致。** 它是宿主接口版本，只在契约不兼容时升；版本不同的插件不会被加载。
5. **页面和状态由宿主呈现。** 插件注册页面、命令、底栏状态，宿主决定放在哪里、什么时候显示。插件不往宿主的标题栏、用户菜单里加东西。
6. **注册即撤销。** 每个 `Register*` 都返回 `IDisposable`；`StopAsync` 里释放，插件被停用或升级时不留残余。

## 最少的代码

模块是一个实现 `IClientPluginModule` 的类：在 `StartAsync` 中通过 `context.RegisterPage(...)` 注册页面（指定图标名、默认排序 `Order`，原生 WebView 等不可重建视图可设 `KeepMounted: true`），并在 `StopAsync` 中释放返回的 `IDisposable`。插件私有服务注册在 `ConfigureServices`，只在本插件的服务容器内可见。完整示例直接参见 `Source/Client.Controls.Gallery.Plugin/GalleryModule.cs`。

包目录根目录的 `plugin.json` 声明包身份（`schema_version`、`id`、`display_name`、`version`、`host_api`、`update_mode` 与 `modules` 入口）。项目文件通过 `EnableDynamicLoading`、共享引用 `Private="false"`/`ExcludeAssets="runtime"` 及构建后写出 `plugin.json` 将其串起；新增插件可直接参考 `Heron.Plugin.Gallery.csproj`，修改 `PluginId`、程序集名与入口类即可。执行 `dotnet build Source/Client.Controls.Gallery.Plugin -c Release` 后，包输出至 `Binaries/Heron/Plugins/<id>/<version>/`。

## 装配

宿主只加载版型里列出的插件，把包放进某个目录不会让它生效。

**用 Heron 启动器试一个包**：不需要任何产品宿主。

```powershell
dotnet run --project Source/Client.Host -- --plugins ../Binaries/Heron/Plugins/heron.gallery/0.2.12 --smoke
```

`--plugins` 可以重复，指向包目录或包含若干包的根目录；`--smoke` 会启动、逐个报告并在任何插件失败时以非零码退出。没有给版型时，找到的包全部启用。

**用版型选择和排序**：在版型 JSON 的 `plugins` 数组中列出插件项（如 `id`、`title`、`enabled_by_default`，外部包加 `is_external_package: true` 与相对 `package_path`），然后通过 `dotnet run --project Source/Client.Host -- --edition my-edition.json` 启动。

版型点名但找不到包的插件，启动直接失败，不会退回旧产物。产品宿主用同一套装配：在它的版型里加一项，插件就以页面形式出现；删掉这一项就拔掉，宿主代码不动。

## 验证

- `--smoke` 打印每个插件的状态和注册的页面，任何插件失败时退出码非零。
- `--capture <目录>` 无界面渲染窗口，输出明暗两张截图，用于检查页面在宿主里的样子。
- 插件只验证自己的页面和行为；宿主的标题栏、菜单和登录由宿主验证。
