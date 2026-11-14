using Avalonia.Controls;
using Avalonia.Interactivity;
using Heron.ClientControls.Notifications;

namespace Heron.ClientControls.Gallery.Pages;

public sealed partial class ToastThemesPage : UserControl, IDisposable
{
    private readonly ToastManager _lightManager = new();
    private readonly ToastManager _darkManager = new();

    public ToastThemesPage()
    {
        InitializeComponent();
        LightToasts.Manager = _lightManager;
        DarkToasts.Manager = _darkManager;
        ShowPreviews();
    }

    private void ResetPreviews(object? sender, RoutedEventArgs e) => ShowPreviews();

    public void Dispose()
    {
        _lightManager.DismissAll();
        _darkManager.DismissAll();
    }

    private void ShowPreviews()
    {
        foreach (var manager in new[] { _lightManager, _darkManager })
        {
            manager.DismissAll();
            foreach (var (kind, title, message) in new[]
                     {
                         (ToastKind.Information, "信息通知", "任务已加入队列，等待执行。"),
                         (ToastKind.Success, "任务完成", "构建产物已准备好。"),
                         (ToastKind.Warning, "需要注意", "本地配置与服务器目录不一致。"),
                         (ToastKind.Error, "执行失败", "示例操作未完成，请检查输入后重试。"),
                         (ToastKind.Success, "长文本通知示例",
                             "Analysis complete. Here's the report.\n\n---\n\n# 示例说明\n\n## 诊断结论\n\n构建：示例任务\n结果：分析已完成，详细报告已保存。\n\n长正文限制高度，滚动查看剩余内容。")
                     })
            {
                manager.Show(new ToastOptions { Kind = kind, Title = title, Message = message, Duration = null });
            }
        }
    }
}
