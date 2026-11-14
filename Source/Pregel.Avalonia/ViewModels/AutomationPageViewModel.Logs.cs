using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heron.Pregel;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    [ObservableProperty] private bool _showRunLogs;
    [ObservableProperty] private bool _isRunReportExpanded;

    [RelayCommand]
    private void CloseLogs() => IsRunReportExpanded = false;

    [RelayCommand]
    private void ShowRunReport()
    {
        ShowRunLogs = false;
        IsRunReportExpanded = true;
    }

    [RelayCommand]
    private void OpenEvidence(AutomationRunItemViewModel? run)
    {
        if (run != null) SelectedRun = run;
        if (SelectedRun == null) return;
        ShowPlan = false;
        PageTabIndex = 2;
        Logs.SetRun(SelectedRun.Record);
        Logs.SelectScope("");
        RunViewIndex = 0;
        ShowRunLogs = true;
        IsRunReportExpanded = true;
    }

    [RelayCommand]
    private void OpenStepLogs(AutomationStepItemViewModel? step)
    {
        if (!HasRun || step == null || SelectedRun == null) return;
        var key = Find(SelectedRun.Steps, "");
        if (key == null) return;
        Logs.SetRun(SelectedRun.Record);
        Logs.SelectScope(key);
        RunViewIndex = 0;
        ShowRunLogs = true;
        IsRunReportExpanded = true;

        string? Find(IEnumerable<AutomationStepItemViewModel> items, string prefix)
        {
            foreach (var item in items)
            {
                var path = prefix + "/" + item.Key;
                if (ReferenceEquals(item, step)) return path;
                if (Find(item.Children, path) is { } found) return found;
            }
            return null;
        }
    }

    private async Task ExportEvidenceAsync(AutomationRunItemViewModel? run)
    {
        // Capture the selected execution before exporting; switching rows must not change the log.
        var record = (run ?? SelectedRun)?.Record;
        if (record == null || string.IsNullOrEmpty(record.RunId)) return;
        try
        {
            var path = await Task.Run(() => ExportRunLog(record));
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else await WindowsFolderService.OpenAsync(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Automation] export run log failed");
            throw;
        }
    }

    private static string ExportRunLog(AutomationRunRecord record)
    {
        // The export has its own directory; never overwrite any graph's original run.log.
        var directory = new FilePregelRunObserver(Path.Combine(ClientHostProfile.LogsDirectory, "Automation"))
            .GetRunDirectory(new PregelRunId(record.RunId));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "run.log");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine($"{record.TaskSnapshot?.DisplayName ?? record.TaskId} · {PregelRunLogFormatter.Status(record.Status.ToString())}");
        writer.WriteLine($"触发：{record.Trigger switch { "manual" => "手动", "schedule" => "定时", "tray" => "托盘", _ => record.Trigger }}");
        writer.WriteLine($"排队：{Time(record.QueuedAt)}");
        writer.WriteLine($"开始：{Time(record.StartedAt)}");
        writer.WriteLine($"结束：{Time(record.EndedAt)}");
        if (record.StartedAt is { } started && record.EndedAt is { } ended && ended >= started)
            writer.WriteLine($"耗时：{PregelRunLogFormatter.Duration(ended - started)}");
        writer.WriteLine($"摘要：{record.Summary}");
        writer.WriteLine($"运行编号：{record.RunId}");
        writer.WriteLine();
        writer.WriteLine("执行步骤");
        WriteSteps(record.Steps, "");
        if (record.EvidenceDirectories.Count == 0)
            writer.WriteLine("[原始图日志未记录，仅包含运行记录与步骤状态]");
        var titles = NodeTitles(record.Steps).GroupBy(pair => pair.Node, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.Ordinal);
        foreach (var evidence in record.EvidenceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine();
            writer.WriteLine("执行时间线");
            writer.WriteLine($"日志目录：{evidence}");
            try
            {
                // Read events for live output (including partial lines). A resumed legacy log retains its old prefix.
                var events = Path.Combine(evidence, "events.jsonl");
                var log = Path.Combine(evidence, "run.log");
                var legacy = false;
                if (File.Exists(log))
                {
                    using var header = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete));
                    legacy = header.ReadLine()?.TrimStart().StartsWith('{') == true;
                }
                var file = File.Exists(events) && !legacy ? events : log;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var formatter = new PregelRunLogFormatter(node => titles.GetValueOrDefault(node.Value));
                while (reader.ReadLine() is { } line)
                {
                    if (!line.TrimStart().StartsWith('{'))
                    {
                        foreach (var text in formatter.Flush()) writer.WriteLine(text);
                        writer.WriteLine(line);
                        continue;
                    }
                    try
                    {
                        if (JsonSerializer.Deserialize<PregelRunEvent>(line) is { Kind.Length: > 0 } value)
                        {
                            foreach (var text in formatter.Format(value)) writer.WriteLine(text);
                        }
                        else writer.WriteLine("[事件记录缺少类型，请查看原始日志]");
                    }
                    catch (JsonException) { writer.WriteLine("[事件记录不完整，请查看原始日志]"); }
                }
                foreach (var text in formatter.Flush()) writer.WriteLine(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writer.WriteLine($"[原始图日志不可读取：{ex.Message}]");
            }
        }
        return path;

        static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";

        static IEnumerable<(string Node, string Title)> NodeTitles(IReadOnlyList<AutomationStepRecord> steps)
        {
            foreach (var step in steps)
            {
                foreach (var child in NodeTitles(step.Children)) yield return child;
                foreach (var node in step.NodeIds) yield return (node, step.Title);
            }
        }

        void WriteSteps(IReadOnlyList<AutomationStepRecord> steps, string indent)
        {
            var index = 0;
            foreach (var step in AutomationStepTopology.InDependencyOrder(steps))
            {
                var duration = step.StartedAt is { } from && step.EndedAt is { } to && to >= from
                    ? " · " + PregelRunLogFormatter.Duration(to - from) : "";
                writer.WriteLine($"{indent}{++index}. {step.Title} · {PregelRunLogFormatter.Status(step.State.ToString())}{duration}");
                if (!string.IsNullOrWhiteSpace(step.Detail)) WriteDetail(step.Detail, indent + "   ");
                if (!string.IsNullOrWhiteSpace(step.Error)) WriteDetail("错误：" + step.Error, indent + "   ");
                WriteSteps(step.Children, indent + "  ");
            }
        }

        void WriteDetail(string text, string indent)
        {
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) writer.WriteLine(indent + line);
        }
    }
}
