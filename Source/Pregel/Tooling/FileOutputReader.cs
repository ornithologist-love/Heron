using System.Runtime.CompilerServices;
using System.Text;

namespace Heron.Pregel.Tooling;

public sealed record FileOutputCursor(long Offset, DateTime CreationTimeUtc, byte[] Boundary);

/// <summary>Reads appended text without moving or rewriting the source file. Missing files are awaited.</summary>
public static class FileOutputReader
{
    /// <summary>Capture before starting a producer to distinguish existing content from a newly rewritten log.</summary>
    public static FileOutputCursor Capture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = stream.Length;
            return new(offset, File.GetCreationTimeUtc(path), Boundary(stream, offset));
        }
        catch (FileNotFoundException) { return new(0, default, []); }
        catch (DirectoryNotFoundException) { return new(0, default, []); }
    }

    private static byte[] Boundary(FileStream stream, long offset)
    {
        var position = stream.Position;
        try
        {
            var bytes = new byte[(int)Math.Min(offset, 64)];
            stream.Position = offset - bytes.Length;
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) return bytes[..count];
                count += read;
            }
            return bytes;
        }
        finally { stream.Position = position; }
    }

    public static async IAsyncEnumerable<string> ReadAsync(string path, long startOffset = 0,
        TimeSpan? pollInterval = null, [EnumeratorCancellation] CancellationToken cancellationToken = default,
        FileOutputCursor? cursor = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        StreamReader? reader = null;
        DateTime creation = default;
        var buffer = new char[4096];
        byte[] boundary = [];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                if (reader != null && (!info.Exists || info.CreationTimeUtc != creation || info.Length < reader.BaseStream.Position
                    || !Boundary((FileStream)reader.BaseStream, reader.BaseStream.Position).AsSpan().SequenceEqual(boundary)))
                {
                    reader.Dispose();
                    reader = null;
                    startOffset = 0;
                }
                if (reader == null && info.Exists)
                {
                    FileStream? stream = null;
                    try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    if (stream != null)
                    {
                        if (cursor is not null)
                        {
                            startOffset = info.CreationTimeUtc == cursor.CreationTimeUtc && stream.Length >= cursor.Offset
                                && Boundary(stream, cursor.Offset).AsSpan().SequenceEqual(cursor.Boundary) ? cursor.Offset : 0;
                            cursor = null;
                        }
                        stream.Position = startOffset <= stream.Length ? startOffset : 0;
                        reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: startOffset == 0);
                        creation = info.CreationTimeUtc;
                        boundary = Boundary(stream, stream.Position);
                    }
                }
                if (reader != null)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    boundary = Boundary((FileStream)reader.BaseStream, reader.BaseStream.Position);
                    if (count > 0)
                    {
                        yield return new string(buffer, 0, count);
                        continue;
                    }
                }
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { reader?.Dispose(); }
    }

    public static async Task ForwardAsync(string path, PregelNodeContext context,
        CancellationToken cancellationToken = default, long startOffset = 0)
    {
        await foreach (var text in ReadAsync(path, startOffset, cancellationToken: cancellationToken).ConfigureAwait(false))
            context.Output(text, path);
    }
}
