using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientHost;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplyStartupTheme();
            var model = new HostModel();
            desktop.MainWindow = new HostWindow(model);
            desktop.ShutdownRequested += async (_, _) => await model.DisposeAsync();
            Dispatcher.UIThread.Post(async () =>
            {
                await model.InitializeAsync();
                if (Program.Options.Smoke) desktop.Shutdown(model.ReportSmoke());
            });
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Profile and theme must be in place before the first window is built.</summary>
    internal void ApplyStartupTheme()
    {
        {
            ClientHostProfile.Configure("Heron", "HeronClientHost", "Heron Client Host",
                Path.Combine(HostSettings.Root, "logs"), Path.Combine(HostSettings.Root, "cache"),
                mode => HostSettings.Update(settings => settings.ThemeMode = mode));
            if (PlatformSettings != null)
                PlatformSettings.ColorValuesChanged += (_, values) => Dispatcher.UIThread.Post(() =>
                    ThemeService.UpdateSystemTheme(values.ThemeVariant == PlatformThemeVariant.Dark ? "dark" : "light"));
            var system = PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? "dark" : "light";
            ThemeService.Initialize(HostSettings.Load().ThemeMode, system);
            ApplyTheme(ThemeService.Current);
            ThemeService.ThemeChanged += ApplyTheme;
        }
    }

    private void ApplyTheme(string theme) =>
        RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
}
