using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Heron.ClientFramework.Infrastructure;

public enum DesktopNotificationKind
{
    Success,
    Warning,
    Error,
    Information
}

/// <summary>
///     Avalonia 桌面通知。通知跟随主窗口所在屏幕，并使用目标屏幕的缩放率把 DIP
///     转换为物理像素，避免多屏/高 DPI 环境下错位。
/// </summary>
public sealed class DesktopNotificationService
{
    private const double EdgeMarginDip = 20;
    private const double StackGapDip = 4;

    private readonly List<NotificationEntry> _active = [];
    private bool _initialized;

    private DesktopNotificationService()
    {
    }

    public static DesktopNotificationService Instance { get; } = new();

    public bool Initialize()
    {
        _initialized = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;
        return _initialized;
    }

    public static void ShowDownloadResult(
        string source,
        string packageName,
        string detail,
        DesktopNotificationKind kind)
    {
        var title = kind switch
        {
            DesktopNotificationKind.Success => $"{source}完成",
            DesktopNotificationKind.Warning => $"{source}已结束",
            _ => $"{source}失败"
        };
        var message = string.IsNullOrWhiteSpace(packageName)
            ? detail
            : $"{packageName}\n{detail}";

        Instance.Show(title, message, kind);
    }

    public bool Show(
        string title,
        string message,
        DesktopNotificationKind kind = DesktopNotificationKind.Success,
        Action? bodyAction = null)
    {
        if (!_initialized && !Initialize())
            return false;

        Dispatcher.UIThread.Post(() => ShowOnUiThread(title, message, kind, bodyAction));
        return true;
    }

    public void Shutdown()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Shutdown);
            return;
        }

        foreach (var entry in _active.ToArray())
            entry.Window.CloseImmediately();

        _active.Clear();
        _initialized = false;
    }

    private void ShowOnUiThread(
        string title,
        string message,
        DesktopNotificationKind kind,
        Action? bodyAction)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var owner = desktop.MainWindow;
            var screens = owner?.Screens;
            var screen = owner != null ? screens?.ScreenFromWindow(owner) : null;
            screen ??= screens?.Primary;
            if (screen == null)
                return;

            var window = new DesktopNotificationWindow(title, message, kind, bodyAction);
            var entry = new NotificationEntry(window, screen);
            window.ActivateRequested += (_, _) =>
            {
                if (desktop.MainWindow is { } mainWindow)
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                }
            };
            window.Closed += (_, _) => Remove(entry);
            window.Opened += (_, _) => RepositionScreen(screen);
            window.SizeChanged += (_, _) => RepositionScreen(screen);

            _active.Add(entry);
            window.Show();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to show Avalonia desktop notification: {ex}");
        }
    }

    private void Remove(NotificationEntry entry)
    {
        if (!_active.Remove(entry))
            return;

        RepositionScreen(entry.Screen);
    }

    private void RepositionScreen(Screen screen)
    {
        var entries = _active.Where(entry => ReferenceEquals(entry.Screen, screen)).ToArray();
        var bottomOffsetDip = EdgeMarginDip;
        for (var index = entries.Length - 1; index >= 0; index--)
        {
            var window = entries[index].Window;
            var widthDip = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
            var heightDip = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
            window.Position = DesktopNotificationPlacement.CalculateFromBottom(
                screen.WorkingArea,
                widthDip,
                heightDip,
                screen.Scaling,
                bottomOffsetDip);
            bottomOffsetDip += heightDip + StackGapDip;
        }
    }

    private sealed record NotificationEntry(DesktopNotificationWindow Window, Screen Screen);
}

public static class DesktopNotificationPlacement
{
    public static PixelPoint Calculate(
        PixelRect workingArea,
        double widthDip,
        double heightDip,
        double scaling,
        int stackIndex,
        double edgeMarginDip = 20,
        double stackGapDip = 4)
    {
        stackIndex = Math.Max(0, stackIndex);
        var bottomOffsetDip = edgeMarginDip + stackIndex * (heightDip + stackGapDip);
        return CalculateFromBottom(workingArea, widthDip, heightDip, scaling, bottomOffsetDip, edgeMarginDip);
    }

    public static PixelPoint CalculateFromBottom(
        PixelRect workingArea,
        double widthDip,
        double heightDip,
        double scaling,
        double bottomOffsetDip,
        double edgeMarginDip = 20)
    {
        scaling = scaling > 0 ? scaling : 1;

        var widthPixels = (int)Math.Ceiling(widthDip * scaling);
        var heightPixels = (int)Math.Ceiling(heightDip * scaling);
        var edgePixels = (int)Math.Round(edgeMarginDip * scaling);
        var bottomOffsetPixels = (int)Math.Round(Math.Max(0, bottomOffsetDip) * scaling);
        var x = workingArea.Right - widthPixels - edgePixels;
        var y = workingArea.Bottom - heightPixels - bottomOffsetPixels;

        return new PixelPoint(
            Math.Max(workingArea.X + edgePixels, x),
            Math.Max(workingArea.Y + edgePixels, y));
    }
}
