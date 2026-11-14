using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Heron.Pregel;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed record AutomationLogScope(string Key, string Title, IReadOnlyList<HashSet<string>> Path)
{
    public override string ToString() => Title;
}

internal sealed record AutomationLogLine(long Number, string Text);
internal sealed record AutomationLogPage(AutomationLogLine[] Lines, long Total, long? FailureLine, string Notice, bool CaughtUp);

/// <summary>One background reader per selection. Disk remains authoritative; only a bounded text window is retained.</summary>
internal sealed class AutomationLogReader(AutomationLogScope scope, IReadOnlyList<AutomationStepRecord> steps) : IDisposable
{
    private const int MaxLines = 5000;
    private const int MaxCharacters = 2 * 1024 * 1024;
    private readonly Dictionary<string, Cursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<AutomationLogLine> _lines = new();
    private readonly PregelRunLogFormatter _formatter = CreateFormatter(steps);
    private int _characters;
    private long _total;
    private long? _failureLine;
    private string _notice = "";
    private bool _flushed;

    public AutomationLogPage Read(AutomationRunRecord record, long? endLine, CancellationToken token)
    {
        _notice = "";
        var caughtUp = true;
        foreach (var directory in record.EvidenceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (!_files.TryGetValue(directory, out var cursor))
            {
                var path = ResolveLog(directory);
                if (path == null) continue; // A running graph may not have emitted its first event yet.
                _files[directory] = cursor = new Cursor(path);
            }
            if (!cursor.Read(line => Consume(line, endLine, token), record.IsFinished, token))
            {
                caughtUp = false;
                break;
            }
        }
        if (caughtUp && record.IsFinished && !_flushed)
        {
            foreach (var line in _formatter.Flush()) Add(line, endLine);
            _flushed = true;
        }
        if (_files.Count == 0)
            _notice = record.IsFinished ? "没有可读取的原始日志；可导出运行摘要与步骤状态。" : "等待运行日志…";
        return new(_lines.ToArray(), _total, _failureLine, _notice, caughtUp);
    }

    private static string? ResolveLog(string directory)
    {
        var events = Path.Combine(directory, "events.jsonl");
        var log = Path.Combine(directory, "run.log");
        if (File.Exists(log))
        {
            using var header = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete));
            // A resumed legacy run keeps its original JSON prefix only in run.log, as in the existing exporter.
            int first;
            do { first = header.Read(); } while (first >= 0 && char.IsWhiteSpace((char)first));
            if (first == '{') return log;
        }
        return File.Exists(events) ? events : File.Exists(log) ? log : null;
    }

    private void Consume(Stream line, long? endLine, CancellationToken token)
    {
        int first;
        do { first = line.ReadByte(); } while (first is 0xEF or 0xBB or 0xBF or 0x20 or 0x09 or 0x0D);
        if (first < 0) return;
        var contentStart = line.Position - 1;
        line.Position = contentStart;
        if (first != '{')
        {
            if (scope.Path.Count == 0)
            {
                line.Position = 0;
                using var reader = new StreamReader(line, Encoding.UTF8, true, 4096, leaveOpen: true);
                var buffer = new char[16385];
                int count;
                while ((count = reader.ReadBlock(buffer, 0, buffer.Length - 1)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (char.IsHighSurrogate(buffer[count - 1]) && reader.Peek() >= 0)
                        buffer[count++] = (char)reader.Read();
                    Add(new string(buffer, 0, count).TrimEnd('\r'), endLine);
                }
            }
            else _notice = "旧版纯文本日志无法按节点筛选，请切换到全部日志。";
            return;
        }
        try
        {
            var value = JsonSerializer.Deserialize<PregelRunEvent>(line);
            if (value is not { Kind.Length: > 0 }) return;
            var chain = new List<PregelRunEvent> { value };
            while (value.Kind == "subgraph.event" && value.Data is { } nested && chain.Count < 32)
            {
                var child = nested.Deserialize<PregelRunEvent>();
                if (child == null) break;
                chain.Add(child);
                value = child;
            }
            if (scope.Path.Count > chain.Count) return;
            for (var i = 0; i < scope.Path.Count; i++)
                if (chain[i].NodeId is not { } node || !scope.Path[i].Contains(node.Value)) return;
            // Format large process output in pieces rather than building a second full-size list of lines.
            if (value.Kind == "node.output" && value.Message.Length > 16384)
            {
                for (var offset = 0; offset < value.Message.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var length = Math.Min(16384, value.Message.Length - offset);
                    if (offset + length < value.Message.Length && char.IsHighSurrogate(value.Message[offset + length - 1])) length--;
                    foreach (var text in _formatter.Format(value with { Message = value.Message.Substring(offset, length) })) Add(text, endLine);
                    offset += length;
                }
            }
            else foreach (var text in _formatter.Format(value)) Add(text, endLine);
            if (value.Kind == "node.finished" && value.Message == "Failed") _failureLine = _total;
        }
        catch (JsonException) { Add("[无法解析此条事件，请导出或查看原始日志]", endLine); }
    }

    private void Add(string text, long? endLine)
    {
        // Long lines are continued on display rows, never discarded. Older rows remain pageable.
        for (var offset = 0; offset < text.Length || offset == 0;)
        {
            var length = Math.Min(16384, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length--;
            ++_total;
            if (!endLine.HasValue || _total <= endLine.Value)
            {
                var segment = text.Substring(offset, length);
                _lines.Enqueue(new(_total, segment));
                _characters += segment.Length;
                while (_lines.Count > MaxLines || _characters > MaxCharacters)
                    _characters -= _lines.Dequeue().Text.Length;
            }
            offset += length;
            if (length == 0) break;
        }
    }

    private static IEnumerable<(string Id, string Title)> Titles(IEnumerable<AutomationStepRecord> records)
    {
        foreach (var step in records)
        {
            foreach (var pair in Titles(step.Children)) yield return pair;
            foreach (var node in step.NodeIds) yield return (node, step.Title);
        }
    }

    private static PregelRunLogFormatter CreateFormatter(IEnumerable<AutomationStepRecord> steps)
    {
        var titles = Titles(steps).GroupBy(pair => pair.Id).ToDictionary(group => group.Key, group => group.First().Title);
        return new(node => titles.GetValueOrDefault(node.Value));
    }

    public void Dispose()
    {
        foreach (var cursor in _files.Values) cursor.Dispose();
    }

    private sealed class Cursor(string path) : IDisposable
    {
        private readonly FileStream _stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        private readonly byte[] _bytes = new byte[65536];
        private long _lineStart;

        public bool Read(Action<Stream> accept, bool finished, CancellationToken token)
        {
            if (_stream.Length < _stream.Position) throw new IOException("日志文件已被截断，请重新加载日志。");
            // Limit work per refresh, not file/event size. Unfinished lines are retained as disk offsets.
            var available = Math.Min(_stream.Length - _stream.Position, 8L * 1024 * 1024);
            while (available > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = _stream.Read(_bytes, 0, (int)Math.Min(available, _bytes.Length));
                if (count == 0) break;
                available -= count;
                var blockStart = _stream.Position - count;
                for (var i = 0; i < count; i++)
                {
                    if (_bytes[i] == '\n')
                    {
                        Emit(blockStart + i);
                        _lineStart = blockStart + i + 1;
                    }
                }
            }
            var caughtUp = _stream.Position >= _stream.Length;
            if (finished && caughtUp && _stream.Position > _lineStart)
            {
                Emit(_stream.Position);
                _lineStart = _stream.Position;
            }
            return caughtUp;

            void Emit(long end)
            {
                token.ThrowIfCancellationRequested();
                var scanPosition = _stream.Position;
                using var line = new LineStream(_stream, _lineStart, end - _lineStart, token);
                try { accept(line); }
                finally { _stream.Position = scanPosition; }
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    // A JSONL record is read directly from disk, without first allocating a giant UTF-16 JSON string.
    private sealed class LineStream(FileStream file, long start, long length, CancellationToken token) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            file.Position = start + Position;
            var count = file.Read(buffer[..(int)Math.Min(buffer.Length, length - Position)]);
            Position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
