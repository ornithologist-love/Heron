namespace Heron.ClientFramework.Shell;

/// <summary>
///     Text the shell shows on its own controls. Defaults are neutral; a host overrides labels such as its
///     theme names without changing the shell.
/// </summary>
public sealed record ShellText
{
    public string ThemeSystem { get; init; } = "跟随系统";
    public string ThemeLight { get; init; } = "浅色";
    public string ThemeDark { get; init; } = "深色";
    public string ThemeMenuName { get; init; } = "选择主题";
    public string PinSidebar { get; init; } = "固定展开";
    public string UnpinSidebar { get; init; } = "固定收起";
    public string NavigationReorderHint { get; init; } = "固定展开时，长按并拖动可调整顺序";
}
