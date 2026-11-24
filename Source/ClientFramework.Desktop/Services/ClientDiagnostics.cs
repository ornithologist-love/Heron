using System.Diagnostics;
using Serilog;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Release diagnostics without request payloads, cookies or exception messages.</summary>
public static class ClientDiagnostics
{
    public static string Origin(Uri? uri) => uri is { IsAbsoluteUri: true }
        ? $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}" : "unknown";

    public static void Failure(string area, string operation, Exception error) =>
        Log.ForContext("SourceContext", area).Error(
            "{Operation} failed: type={ExceptionType} hresult={HResult} stack={Stack}",
            operation, error.GetType().FullName, error.HResult, error.StackTrace);

    public static async Task<T> ObserveAsync<T>(string area, string operation, Func<Task<T>> action)
    {
        var log = Log.ForContext("SourceContext", area);
        var id = Guid.NewGuid().ToString("N")[..8];
        var watch = Stopwatch.StartNew();
        // Independent of the UI dispatcher: even a blocked WebView await is visible.
        using var timer = new Timer(_ => log.Warning(
            "Operation {OperationId} {Operation} still pending elapsed_ms={Elapsed}",
            id, operation, watch.ElapsedMilliseconds), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
        try
        {
            var result = await action();
            if (watch.ElapsedMilliseconds >= 1000)
                log.Information("Operation {OperationId} {Operation} complete elapsed_ms={Elapsed}", id, operation, watch.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            log.Information("Operation {OperationId} {Operation} cancelled elapsed_ms={Elapsed}", id, operation, watch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception error)
        {
            Failure(area, $"{operation} ({id})", error);
            throw;
        }
    }
}
