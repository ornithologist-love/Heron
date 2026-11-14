using System.Text;

namespace Heron.AutomationTenants;

/// <summary>Frames complete events while preserving the source-file UTF-8 byte identity.</summary>
internal static class Utf8EventLog
{
    internal static (string[] Lines, long Length) Read(string path, long offset)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = file.Length;
        file.Position = offset;
        using var batch = new MemoryStream();
        var buffer = new byte[256 * 1024];
        var lastNewline = -1;
        while (lastNewline < 0)
        {
            var count = file.Read(buffer, 0, buffer.Length);
            if (count == 0) return ([], length); // Keep a partial trailing event for the next poll.
            var start = (int)batch.Length;
            batch.Write(buffer, 0, count);
            var last = Array.LastIndexOf(buffer, (byte)'\n', count - 1, count);
            if (last >= 0) lastNewline = start + last;
        }
        // Preserve CR from Windows CRLF: offset is the exact UTF-8 source-file byte offset.
        var text = Encoding.UTF8.GetString(batch.GetBuffer(), 0, lastNewline);
        return (text.Split('\n'), length);
    }

}
