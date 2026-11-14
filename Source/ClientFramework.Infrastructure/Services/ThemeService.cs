
namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     进程内主题状态中枢（不依赖 Avalonia 类型，供宿主与插件共同引用）：
///     宿主订阅 ThemeChanged 更新 Application.RequestedThemeVariant，Web 插件订阅
///     ThemeChanged 向嵌入页推送 postMessage，配置落盘在此集中完成。
/// </summary>
public static class ThemeService
{
    /// <summary>主题切换事件；参数为 "dark" 或 "light"。</summary>
    public static event Action<string>? ThemeChanged;

    /// <summary>用户选择：system（默认）、light 或 dark；独立于当前实际主题。</summary>
    public static string Mode { get; private set; } = "system";
    private static string _systemTheme = "light";

    /// <summary>当前生效主题：light（暖玉）或 dark（墨玉），供原生和 Web 共用。</summary>
    public static string Current { get; private set; } = "light";

    /// <summary>
    ///     启动时用持久化配置初始化 Current；只设置状态，不触发 ThemeChanged。
    /// </summary>
    public static void Initialize(string mode, string systemTheme = "light")
    {
        Mode = NormalizeMode(mode);
        _systemTheme = systemTheme == "dark" ? "dark" : "light";
        Current = ResolveTheme();
    }

    /// <summary>
    ///     更新当前主题并广播 ThemeChanged；persist=true 时交给宿主登记的持久化回调。
    /// </summary>
    public static void Set(string mode, bool persist)
    {
        Mode = NormalizeMode(mode);
        Current = ResolveTheme();
        if (persist)
        {
            ClientHostProfile.PersistThemeMode?.Invoke(Mode);
        }
        ThemeChanged?.Invoke(Current);
    }

    /// <summary>宿主在 UI 线程报告系统变化；不改写用户选择或配置文件。</summary>
    public static void UpdateSystemTheme(string theme)
    {
        _systemTheme = theme == "dark" ? "dark" : "light";
        var resolved = ResolveTheme();
        if (Current == resolved) return;
        Current = resolved;
        ThemeChanged?.Invoke(Current);
    }

    private static string NormalizeMode(string? mode) => mode is "light" or "dark" ? mode : "system";
    private static string ResolveTheme() => Mode == "system" ? _systemTheme : Mode;
}
