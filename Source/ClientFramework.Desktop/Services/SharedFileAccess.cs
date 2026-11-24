using System.IO;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     Opens files with shared-read access so they can be read while another process is writing.
///
///     The primary use case is the client log. Serilog holds it with <see cref="FileShare.Read"/>,
///     which permits readers but conflicts with the default <c>File.OpenRead</c> sharing mode.
///     Opening with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> allows the
///     log to be read without blocking writes or log rotation.
/// </summary>
public static class SharedFileAccess
{
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
