using Avalonia;

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
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
            .StartWithClassicDesktopLifetime([]);
    }
}
