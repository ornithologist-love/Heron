using System.Diagnostics;

namespace Heron.Pregel.Automation;

internal static class AutomationUpdateLock
{
    [ThreadStatic]
    private static Dictionary<string, Lease>? _held;

    public static IDisposable AcquireFile(string path, TimeSpan timeout, string busyMessage)
    {
        path = Path.GetFullPath(path);
        var held = _held ??= new(StringComparer.Ordinal);
        if (held.TryGetValue(path, out var current))
            return current.AddReference();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var lease = new Lease(stream, () => held.Remove(path));
                held.Add(path, lease);
                return lease.AddReference();
            }
            catch (IOException exception)
            {
                if (started.Elapsed >= timeout) throw new IOException(busyMessage, exception);
                Thread.Sleep(25);
            }
        }
    }

    private sealed class Lease(FileStream file, Action remove)
    {
        private int _references;
        public IDisposable AddReference()
        {
            _references++;
            return new Reference(this);
        }

        private void Release()
        {
            if (--_references != 0) return;
            remove();
            file.Dispose();
        }

        private sealed class Reference(Lease lease) : IDisposable
        {
            private Lease? _lease = lease;
            public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Release();
        }
    }
}
