using Heron.Pregel.Automation;

namespace Heron.AutomationTenants;

public sealed partial class RemoteAutomationAgent
{
    private async Task DeployAsync(RemoteAutomationDeployment request, string server, CancellationToken token)
    {
        async Task<System.Text.Json.JsonElement> Report(string state, string detail, RemoteAutomationTask? task = null)
        {
            return await PostAsync<System.Text.Json.JsonElement>("deployments/" + request.Id, new
            {
                machine_name = Environment.MachineName, state, detail, source = request.Source, task
            }, token, server);
        }
        var receipt = await Report("preparing", "正在拉取指定流程及 Shared，并加载流程模板");
        if (receipt.TryGetProperty("state", out var state) && state.GetString() is "ready" or "failed") return;
        RemoteAutomationTask prepared;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var draft = await _officialScripts.PrepareDeploymentAsync(request, timeout.Token);
            var savedView = automation.Tasks.FirstOrDefault(view => view.Task.Id == request.TaskId);
            var saved = savedView?.Task ?? draft.Task;
            var reason = !File.Exists(settings.CliExecutablePath) ? "当前安装缺少 CLI，请更新构建机包。"
                : !saved.ManualEnabled ? "任务未启用手动触发。"
                : settings.IsObserveOnly(saved.Id) ? "此任务在本机运行，云端仅接收记录。"
                : saved.TriggerConfigurationError ?? "";
            prepared = new RemoteAutomationTask
            {
                Id = request.TaskId, ConfigurationId = saved.Id, DefinitionId = request.Source.SourceId,
                Name = saved.DisplayName, Parameters = saved.Parameters,
                Triggers = savedView == null ? null : TriggersOf(saved),
                ScriptRevision = automation.Scripts.SavedScriptRevision(draft.Task.TemplateId),
                CanRun = reason.Length == 0, UnavailableReason = reason
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            var detail = error is OperationCanceledException ? "流程准备超时，请查询机器状态后重新部署。" : error.Message;
            await Report("failed", detail.Length <= 1000 ? detail : detail[..1000]);
            return;
        }
        // Lost transport acknowledgements leave the durable request pending for idempotent replay.
        await Report("ready", "流程与 Shared 版本核验完成，模板已加载", prepared);
    }
}
