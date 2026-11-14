using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Heron.ClientControls.Gallery;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var capture = Array.IndexOf(args, "--capture");
        if (capture >= 0)
        {
            if (capture + 1 >= args.Length) throw new ArgumentException("--capture 缺少输出目录。");
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia().WithInterFont().SetupWithoutStarting();
            Capture(Path.GetFullPath(args[capture + 1]));
            return 0;
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        var images = new List<object>();
        foreach (var dark in new[] { false, true })
        foreach (var scene in GalleryCatalog.Scenes)
        {
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var view = GalleryCatalog.Create(scene.Id);
            var window = new Window { Width = 1180, Height = 820, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var file = $"{scene.Id}-{(dark ? "dark" : "light")}.png";
                using var bitmap = new RenderTargetBitmap(new PixelSize(1180, 820));
                bitmap.Render(view);
                bitmap.Save(Path.Combine(directory, file), PngBitmapEncoderOptions.Default);
                images.Add(new { file, scene = scene.Id, theme = dark ? "dark" : "light" });
            }
            finally
            {
                window.Content = null;
                window.Close();
                if (view is IDisposable disposable) disposable.Dispose();
            }
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(images, new JsonSerializerOptions { WriteIndented = true }));
    }
}
