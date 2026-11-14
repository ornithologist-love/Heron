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

模块是一个实现 `IClientPluginModule` 的类。Gallery 插件的全部入口：

```csharp
public sealed class GalleryModule : IClientPluginModule
{
    private IDisposable? _page;

    public void ConfigureServices(IServiceCollection services) { }

    public Task StartAsync(IClientPluginContext context, CancellationToken cancellationToken)
    {
        _page = context.RegisterPage(new ClientPluginPage("heron.gallery", "控件 Gallery", "Grid", 900,
            () => new GalleryView(ownsTheme: false)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _page?.Dispose();
        _page = null;
        return Task.CompletedTask;
    }
}
```

`ClientPluginPage` 的图标是 FluentIcons 的名字，`Order` 决定默认排序；`KeepMounted: true` 让页面离开后保留视图（适合原生 WebView 等不能重建的内容）。插件自己的服务注册在 `ConfigureServices`，只在本插件的服务容器里可见。

`plugin.json` 声明包身份和入口：

```json
{
  "schema_version": 1,
  "id": "heron.gallery",
  "display_name": "控件 Gallery",
  "version": "0.2.12",
  "host_api": "4",
  "update_mode": "restart_required",
  "modules": [
    { "name": "Gallery", "assembly": "Heron.Plugin.Gallery.dll",
      "entry_type": "Heron.Plugins.Gallery.GalleryModule", "load": "startup" }
  ]
}
```

项目文件把这些串起来：`EnableDynamicLoading`、共享引用不复制、输出目录就是包目录、构建后按项目版本写出 `plugin.json`。直接照抄 `Heron.Plugin.Gallery.csproj`，改 `PluginId`、程序集名和入口类即可。`dotnet build Source/Client.Controls.Gallery.Plugin -c Release` 后包在 `Binaries/Heron/Plugins/<id>/<version>/`。

## 装配

宿主只加载版型里列出的插件，把包放进某个目录不会让它生效。

**用 Heron 启动器试一个包**：不需要任何产品宿主。

```powershell
dotnet run --project Source/Client.Host -- --plugins ../Binaries/Heron/Plugins/heron.gallery/0.2.12
dotnet run --project Source/Client.Host -- --plugins <插件根目录> --smoke     # 启动、逐个报告、失败即非零退出
```

`--plugins` 可以重复，指向包目录或包含若干包的根目录。没有给版型时，找到的包全部启用。

**用版型选择和排序**：版型列出插件 ID，外部包写相对路径：

```json
{
  "name": "my-edition",
  "plugins": [
    { "id": "heron.gallery", "title": "控件 Gallery", "enabled_by_default": true,
      "is_external_package": true, "package_path": "../Binaries/Heron/Plugins/heron.gallery/0.2.12" }
  ]
}
```

```powershell
dotnet run --project Source/Client.Host -- --edition my-edition.json
```

版型点名但找不到包的插件，启动直接失败，不会退回旧产物。工作区的 `ClientEditions/heron-host.json` 就是这样把 Gallery 和任务管理器组在一起的。产品宿主用同一套装配：在它的版型里加一项，插件就以页面形式出现；删掉这一项就拔掉，宿主代码不动。

## 验证

- `--smoke` 打印每个插件的状态和注册的页面，任何插件失败时退出码非零。
- `--capture <目录>` 无界面渲染窗口，输出明暗两张截图，用于检查页面在宿主里的样子。
- 插件只验证自己的页面和行为；宿主的标题栏、菜单和登录由宿主验证。
