using Heron.ClientControls.Notifications;

namespace Heron.ClientFramework.Infrastructure;

public partial class DesktopNotificationWindow
{
    public static DesktopNotificationWindow CreateLayoutPreview(string state)
    {
        var window = new DesktopNotificationWindow();
        window.Opened -= window.OnOpened;
        window.Topmost = false;
        window.CanResize = true;
        window.MaxHeight = double.PositiveInfinity;
        window.SizeToContent = Avalonia.Controls.SizeToContent.Manual;
        window._notification = window._toastManager.Show(new ToastOptions
        {
            Title = state == "error" ? "执行失败" : "任务已完成",
            Message = state == "long" ? string.Concat(System.Linq.Enumerable.Repeat("长文本通知预览：检查换行、内容高度与操作区域。", 8))
                : state == "error" ? "目标设备连接不可用，请检查配置后重试。" : "构建与数据采集已完成，结果已保存到本地。",
            Kind = state == "error" ? ToastKind.Error : ToastKind.Success, Duration = null
        });
        return window;
    }
}
