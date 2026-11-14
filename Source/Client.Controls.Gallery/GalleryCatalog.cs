using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Heron.ClientControls.Text;

namespace Heron.ClientControls.Gallery;

internal sealed record GalleryScene(string Id, string Group, string Title);

internal static class GalleryCatalog
{
    internal static readonly GalleryScene[] Scenes =
    [
        new("components", "基础控件", "输入与表格"),
        new("icons", "基础控件", "图标按钮"),
        new("colors", "基础控件", "颜色 Token"),
        new("markdown", "基础控件", "Markdown"),
        new("chrome", "基础控件", "窗口外壳"),
        new("toasts", "反馈与弹层", "Toast"),
        new("dialogs", "反馈与弹层", "模态框"),
        new("popups", "反馈与弹层", "Popup"),
        new("automation-mock", "自动化 Mock", "任务与执行流程")
    ];

    internal static Control Create(string scene) => scene switch
    {
        "components" => new Pages.ComponentsPreviewPage(),
        "icons" => new Pages.IconButtonPage(),
        "colors" => new Pages.ColorSystemPage(),
        "toasts" => new Pages.ToastThemesPage(),
        "dialogs" => new Pages.ModalDialogPage(),
        "popups" => new Pages.PopupComparisonPage(),
        "automation-mock" => new Pages.AutomationMockPage(),
        "markdown" => Markdown(),
        "chrome" => new Pages.ChromePage(),
        _ => throw new ArgumentException("未知的控件展示页。", nameof(scene))
    };

    private static Control Markdown()
    {
        var text = new SelectableMarkdownTextBlock
        {
            Margin = new Avalonia.Thickness(24), FontSize = 14,
            Markdown = "# Markdown 文本\n\n支持 **粗体**、*斜体*、`行内代码`与文本选择。\n\n- 第一项示例\n- 第二项示例\n\n> 这是一段通用的说明文字。"
        };
        text.Bind(SelectableMarkdownTextBlock.ForegroundProperty, new DynamicResourceExtension("FgDefaultBrush"));
        return new ScrollViewer { Content = text };
    }
}
