using Heron.Pregel.Automation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Heron.AutomationTenants;

public sealed partial class RemoteAutomationAgent
{
    private Dictionary<string, Delivery> _controls = [];

    private void LoadControls()
    {
        var path = Path.Combine(_root, "controls.json");
        _controls = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Delivery>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    private void SaveControls()
    {
        var path = Path.Combine(_root, "controls.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_controls, Json));
        File.Move(path + ".tmp", path, true);
    }

    private async Task ApplyControlAsync(RemoteAutomationControl request, CancellationToken token)
    {
        if (request.Id.Length != 32 || !request.Id.All(Uri.IsHexDigit)
            || request.RunId.Length != 32 || !request.RunId.All(Uri.IsHexDigit))
            throw new InvalidDataException("远程处理请求身份无效");
        var record = automation.FindRun(request.TaskId, request.LocalRunId);
        if (record == null)
        {
            await CompleteControlAsync(request, null, "failed", "本机已找不到这条运行记录，未执行处理。", token);
            return;
        }
        if (request.Action == "discard")
        {
            // Needs neither the graph nor a worker process, so it still works when those are what broke.
            var closed = automation.Discard(record.TaskId, record.RunId, "已由服务器强制结束，未执行收尾。");
            await CompleteControlAsync(request, automation.FindRun(record.TaskId, record.RunId) ?? record,
                closed ? "applied" : "failed", closed ? "本机记录已结束，任务可以再次运行。" : "本次运行仍在执行，请先停止。", token);
            return;
        }
        if (record.LastRemoteActionId == request.Id)
        {
            // The queue marker proves admission, not completion. Let the original worker
            // finish before deciding whether it preserved the same review after an error.
            if (record.OwnerIsAlive && !record.IsFinished) return;
            var unchangedReview = record.Status == AutomationRunStatus.AwaitingReview
                && record.Review is { } pending && (request.Action == "stop"
                    || pending.Id == request.ReviewId && pending.ContentHash == request.ContentHash);
            await CompleteControlAsync(request, record, unchangedReview ? "failed" : "applied",
                unchangedReview ? "处理未完成，原记录已保留：" + record.Summary : "处理决定已交给原运行，执行结果继续上报。", token);
            return;
        }
        if (request.Action == "stop" && record.Status != AutomationRunStatus.AwaitingReview)
        {
            automation.CancelRemote(record.TaskId, record.RunId);
            await CompleteControlAsync(request, record, "applied", record.IsFinished ? "本次运行已结束。" : "已请求停止，等待原运行完成收尾。", token);
            return;
        }
        if (!_controls.TryGetValue(request.Id, out var delivery))
        {
            delivery = new() { TaskId = request.TaskId };
            _controls.Add(request.Id, delivery);
            SaveControls();
        }
        if (Alive(delivery)) return;
        if (delivery.ProcessId != 0 && delivery.Failure.Length == 0)
            delivery.Failure = "处理进程已退出，未登记处理结果。" + ReadLaunchError(request.Id);
        if (delivery.Failure.Length > 0)
        {
            await CompleteControlAsync(request, record, "failed", delivery.Failure, token);
            return;
        }
        try
        {
            if (record.Status != AutomationRunStatus.AwaitingReview || record.Review is not { } review)
                throw new InvalidOperationException("该运行已不在等待审核，请刷新后重试。");
            var action = request.Action switch
            {
                "approve" => AutomationReviewAction.Approve,
                "revise" => AutomationReviewAction.Revise,
                "abandon" or "stop" => AutomationReviewAction.Abandon,
                _ => throw new InvalidOperationException("不支持的远程处理动作。")
            };
            if (request.Action != "stop" && (review.Id != request.ReviewId || review.ContentHash != request.ContentHash))
                throw new InvalidOperationException("审核内容已经变化，请阅读新版本后重新处理。");
            if (action == AutomationReviewAction.Abandon && record.RemoteRequestId is { } remote)
            {
                // Abandon already enters Finally. A later transport cancellation must not
                // become a second stop that interrupts cleanup.
                if (!_deliveries.TryGetValue(remote, out var original)) _deliveries[remote] = original = new() { TaskId = record.TaskId };
                original.StopSent = true;
                Save();
            }
            StartReview(request, new(review.Id, review.ContentHash, action, request.Feedback) { RemoteActionId = request.Id }, delivery);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            delivery.Failure = error.Message;
            SaveControls();
            await CompleteControlAsync(request, record, "failed", delivery.Failure, token);
        }
    }

    private async Task CompleteControlAsync(RemoteAutomationControl request, AutomationRunRecord? record,
        string state, string detail, CancellationToken token)
    {
        if (record != null)
            await PostAsync<JsonElement>($"runs/{request.RunId}/report", new
            {
                machine_name = Environment.MachineName, local_run_id = record.RunId, snapshot = record, output = record.EncodedOutput
            }, token);
        await PostAsync<JsonElement>($"runs/{request.RunId}/controls/{request.Id}", new
        {
            machine_name = Environment.MachineName, state, detail
        }, token);
    }

    private void StartReview(RemoteAutomationControl request, AutomationReviewDecision decision, Delivery delivery)
    {
        if (!File.Exists(settings.CliExecutablePath)) throw new FileNotFoundException("当前安装缺少 CLI，请更新构建机包。");
        var directory = RequestDirectory(request.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "review.json");
        File.WriteAllText(path, JsonSerializer.Serialize(decision, Json));
        var instance = environment.ClientInstance();
        var start = new ProcessStartInfo(settings.CliExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var argument in settings.CliArguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "graph", "review", "--task", request.TaskId, "--run", request.LocalRunId,
                     "--review-file", path, "--remote-output-file", Path.Combine(directory, "launch.log"),
                     "--yes", "--client-instance", instance }) start.ArgumentList.Add(argument);
        // The host starts and owns the detached CLI; it applies product-specific process settings.
        using var process = environment.StartProcess(start, _server,
            RemotePregelResourceCoordinator.IsEnabled(_server));
        delivery.ProcessId = process.Id;
        delivery.StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        SaveControls();
    }
}
