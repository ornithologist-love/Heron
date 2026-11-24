using Avalonia;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientHost;

internal static class Program
{
    internal static HostOptions Options { get; private set; } = new([], null, false);

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Options = HostOptions.Parse(args);
            if (Options.Assembles) HostAssembly.Run(Options, AppContext.BaseDirectory);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        if (Options.Capture != null) return HostCapture.Run(Options.Capture, Options.Width);

        SingleInstanceGuard? guard = null;
        if (!Options.Smoke)
        {
            var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Heron.ClientHost");
            var instance = SingleInstanceOptions.Create(
                "Heron_ClientHost",
                executablePath,
                HostSettings.Root,
                isolateDebug: true,
                pidFileName: "heron-host.pid",
                mainWindowTitle: "Heron");
            if (!SingleInstanceGuard.TryAcquire(instance, out guard)) return 0;
        }

        App.InstanceGuard = guard;
        try
        {
            return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            App.InstanceGuard = null;
            guard?.Dispose();
        }
    }
}
