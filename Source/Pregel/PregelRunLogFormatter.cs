using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heron.Pregel;

/// <summary>Readable event timeline shared by file logs, consoles and history exports. One instance per reader.</summary>
public sealed class PregelRunLogFormatter(Func<NodeId, string?>? resolveNodeTitle = null)
{
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PregelRunEvent Event, string Text)> _output = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Timestamp, bool Complete)> _progress = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Format(PregelRunEvent value)
    {
        var lines = new List<string>();
        Format(value, lines, 0);
        return lines;
    }

    public IReadOnlyList<string> Flush()
    {
        var lines = new List<string>();
        FlushOutput(lines, _ => true);
        return lines;
    }

    /// <summary>Resource usage explaining why a run is queued, shared by logs and host history.</summary>
    public static string WaitingSummary(PregelRunEvent value)
    {
        if (value.Data is { ValueKind: JsonValueKind.Array } data)
        {
            try
            {
                if (data.Deserialize<PregelMutexCoordinator.WaitState[]>() is { Length: > 0 } resources)
                    return "等待资源：" + string.Join("、", resources.Select(resource =>
                        $"{resource.Key}（{resource.Used}/{resource.MaxConcurrency}）"
                        + (resource.QueuedAhead ? "，前方有排队运行" : "")));
            }
            catch (JsonException) { }
        }
        return "等待资源空闲";
    }

    private void Format(PregelRunEvent value, List<string> lines, int depth)
    {
        if (value.Kind == "subgraph.event" && value.Data is { } nested && depth < 32)
        {
            try
            {
                if (nested.Deserialize<PregelRunEvent>() is { } child)
                {
                    Format(child, lines, depth + 1);
                    return;
                }
            }
            catch (JsonException) { }
            Add(lines, value, "子图事件无法解析；请查看 events.jsonl");
            return;
        }

        var key = NodeKey(value);
        switch (value.Kind)
        {
            case "run.started":
                _started[RunKey(value)] = value.Timestamp;
                Add(lines, value, $"图开始：{value.Message}（{value.Timestamp.ToLocalTime():yyyy-MM-dd zzz}）");
                break;
            case "run.waiting":
                Add(lines, value, WaitingSummary(value));
                break;
            case "run.finished":
                FlushOutput(lines, item => item.RunId == value.RunId);
                Add(lines, value, "图结束：" + Status(value.Message) + Elapsed(RunKey(value), value.Timestamp));
                WriteError(lines, value);
                break;
            case "node.started":
                _started[key] = value.Timestamp;
                Add(lines, value, "开始" + (value.AttemptNumber > 1 ? $"（第 {value.AttemptNumber} 次尝试）" : ""));
                break;
            case "node.finished":
                FlushOutput(lines, item => NodeKey(item) == key);
                _progress.Remove(key);
                var reason = value.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("Reason", out var property)
                    ? property.GetString() : null;
                Add(lines, value, Status(value.Message) + Elapsed(key, value.Timestamp)
                    + (string.IsNullOrWhiteSpace(reason) ? "" : "：" + reason));
                WriteError(lines, value);
                break;
            case "node.retry":
                var delay = value.Data is { ValueKind: JsonValueKind.Object } retry && retry.TryGetProperty("DelaySeconds", out var seconds)
                    && seconds.TryGetDouble(out var number) && double.IsFinite(number)
                    ? "，等待 " + Duration(TimeSpan.FromSeconds(Math.Max(0, number))) : "";
                Add(lines, value, $"准备第 {(value.AttemptNumber ?? 1) + 1} 次尝试{delay}");
                break;
            case "node.reused":
                Add(lines, value, "复用原运行结果：" + Status(value.Message));
                WriteError(lines, value);
                break;
            case "node.output":
                Output(lines, value);
                break;
            case PregelProgress.EventKind:
                if (!PregelProgress.TryRead(value, out var progress)) break;
                if (_progress.TryGetValue(key, out var last)
                    && (progress.Fraction == 1 ? last.Complete : value.Timestamp - last.Timestamp < TimeSpan.FromSeconds(1))) break;
                _progress[key] = (value.Timestamp, progress.Fraction == 1);
                Add(lines, value, "进度" + (progress.Fraction is { } fraction ? $" {fraction:P0}" : "")
                    + (string.IsNullOrWhiteSpace(progress.Detail) ? "" : "：" + progress.Detail));
                break;
            case "process.exited":
                FlushOutput(lines, item => NodeKey(item) == key);
                if (value.Data is { ValueKind: JsonValueKind.Object } process && process.TryGetProperty("ExitCode", out var code))
                    Add(lines, value, "进程退出，退出码 " + code);
                break;
            default:
                if (!string.IsNullOrWhiteSpace(value.Message)) Add(lines, value, value.Message);
                WriteError(lines, value);
                break;
        }
    }

    private void Output(List<string> lines, PregelRunEvent value)
    {
        if (value.Source is not ("stdout" or "stderr" or "output"))
        {
            Add(lines, value, value.Message);
            return;
        }
        // Process readers deliver arbitrary chunks; a partial line retains its first chunk's timestamp.
        var key = NodeKey(value) + "/" + value.Source;
        var pending = _output.GetValueOrDefault(key);
        var chunk = value.Message;
        if (pending.Text?.EndsWith('\r') == true)
        {
            Add(lines, pending.Event, pending.Text.TrimEnd('\r'));
            pending = default;
            if (chunk.StartsWith('\n')) chunk = chunk[1..];
        }
        // Keep a terminal CR until the next chunk so split CRLF sequences still form a single newline.
        var terminalCr = chunk.EndsWith('\r');
        if (terminalCr) chunk = chunk[..^1];
        var text = (pending.Text ?? "") + chunk.Replace("\r\n", "\n").Replace('\r', '\n');
        var first = pending.Event ?? value;
        var end = text.LastIndexOf('\n');
        if (end >= 0)
        {
            Add(lines, first, text[..end]);
            text = text[(end + 1)..];
            first = value;
        }
        if (text.Length >= 32768)
        {
            Add(lines, first, text);
            text = "";
        }
        if (terminalCr) text += "\r";
        if (text.Length == 0) _output.Remove(key);
        else _output[key] = (first, text);
    }

    private void FlushOutput(List<string> lines, Func<PregelRunEvent, bool> predicate)
    {
        foreach (var item in _output.Where(item => predicate(item.Value.Event)).OrderBy(item => item.Value.Event.Timestamp).ToArray())
        {
            Add(lines, item.Value.Event, item.Value.Text);
            _output.Remove(item.Key);
        }
    }

    private void Add(List<string> lines, PregelRunEvent value, string text)
    {
        var title = value.NodeTitle ?? (value.NodeId is { } id ? resolveNodeTitle?.Invoke(id) ?? NodeName(id.Value) : null);
        var source = value.Source == "stderr" ? " [标准错误输出]" : "";
        var prefix = $"[{value.Timestamp.ToLocalTime():HH:mm:ss}]" + (string.IsNullOrWhiteSpace(title) ? "" : $" [{title}]") + source + " ";
        var clean = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        foreach (var line in clean.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(prefix + line.TrimEnd());
    }

    private void WriteError(List<string> lines, PregelRunEvent value)
    {
        if (value.Error is { } error)
            Add(lines, value, (value.Message == "Cancelled" ? "说明：" : "错误：") + $"{error.Message}（{error.Code}）");
    }

    private string Elapsed(string key, DateTimeOffset ended)
    {
        if (!_started.Remove(key, out var started) || ended < started) return "";
        return "（" + Duration(ended - started) + "）";
    }

    private static string RunKey(PregelRunEvent value) => value.RunId.Value + "/" + value.ExecutionId;
    private static string NodeKey(PregelRunEvent value) => RunKey(value) + "/" + value.NodeId?.Value + "/" + value.AttemptId?.Value;
    private static string NodeName(string id) => id.StartsWith("task:", StringComparison.Ordinal) ? id.Split(':')[1] : id;

    public static string Status(string status) => status switch
    {
        "Pending" => "等待", "Queued" => "排队中", "Running" => "运行中",
        "Succeeded" or "Completed" => "成功", "Skipped" => "已跳过", "NotRun" => "未执行",
        "Failed" => "失败", "Cancelled" => "已取消", "AwaitingReview" or "Interrupted" => "等待人工处理",
        _ => status
    };

    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1) return $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分 {elapsed.Seconds} 秒";
        if (elapsed.TotalMinutes >= 1) return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
        return elapsed.TotalSeconds >= 1 ? elapsed.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " 秒"
            : elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " 毫秒";
    }
}
