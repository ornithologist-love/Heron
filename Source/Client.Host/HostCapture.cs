using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientHost;

/// <summary>Renders the launcher window headless in light and dark for visual review.</summary>
internal static class HostCapture
{
    public static int Run(string directory, int width)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().WithInterFont().SetupWithoutStarting();
        ((App)Application.Current!).ApplyStartupTheme();
        Directory.CreateDirectory(directory);
        var model = new HostModel();
        Wait(model.InitializeAsync());
        var window = new HostWindow(model) { Width = width, Height = 900 };
        window.Show();
        foreach (var mode in new[] { "light", "dark" })
        {
            ThemeService.Set(mode, persist: false);
            for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(15); }
            window.CaptureRenderedFrame()?.Save(Path.Combine(directory, $"host-{mode}.png"));
        }
        window.Close();
        Wait(model.DisposeAsync().AsTask());
        Console.WriteLine("[capture] " + directory);
        return 0;
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
    }
}
