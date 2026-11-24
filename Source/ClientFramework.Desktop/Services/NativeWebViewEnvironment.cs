using Avalonia.Controls;
using Avalonia.Platform;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
/// Keeps WebView profiles outside the Velopack-managed installation directory.
/// A WebView2 process can briefly outlive the Avalonia control; placing its profile
/// under current would then prevent the updater from replacing that directory.
/// </summary>
public static class NativeWebViewEnvironment
{
    public static string UserDataFolder => Path.Combine(ClientHostProfile.CacheDirectory, "webview2");

    public static void Configure(NativeWebView webView) => Configure(webView, null);

    public static void Configure(NativeWebView webView, Func<Uri>? recoveryUri)
    {
        ArgumentNullException.ThrowIfNull(webView);
        Directory.CreateDirectory(UserDataFolder);
        webView.EnvironmentRequested += OnEnvironmentRequested;
        if (OperatingSystem.IsWindows())
        {
            NativeWebViewRecovery.Attach(webView, recoveryUri);
            NativeWebViewScaling.Attach(webView);
        }
        var id = Guid.NewGuid().ToString("N")[..8];
        var log = Serilog.Log.ForContext("SourceContext", "WebView");
        log.Information("WebView {WebViewId} configured control={Control} profile={Profile}", id, webView.Name, UserDataFolder);
        webView.NavigationStarted += (_, args) => log.Information(
            "WebView {WebViewId} navigation started origin={Origin}", id, ClientDiagnostics.Origin(args.Request));
        webView.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess) log.Information("WebView {WebViewId} navigation completed origin={Origin}", id, ClientDiagnostics.Origin(args.Request));
            else log.Warning("WebView {WebViewId} navigation failed origin={Origin}", id, ClientDiagnostics.Origin(args.Request));
        };
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            windows.UserDataFolder = UserDataFolder;
            Serilog.Log.ForContext("SourceContext", "WebView").Information("WebView environment requested profile={Profile}", UserDataFolder);
        }
    }
}
