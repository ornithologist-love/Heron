namespace Heron.Pregel.Automation;

public sealed record AutomationPermission(string Id, string Title, string Detail);

public sealed partial class AutomationHost
{
    private readonly Dictionary<string, (string TaskId, AutomationPermission Request, TaskCompletionSource<bool> Reply)> _permissions = [];

    public AutomationPermission? PendingPermission(string taskId, string runId)
    {
        lock (_gate) return _permissions.TryGetValue(runId, out var pending) && pending.TaskId == taskId ? pending.Request : null;
    }

    public void AnswerPermission(string taskId, string runId, string permissionId, bool allow)
    {
        lock (_gate)
        {
            if (!_permissions.TryGetValue(runId, out var pending) || pending.TaskId != taskId || pending.Request.Id != permissionId)
                throw new InvalidOperationException("工具请求已结束，请刷新后重试");
            pending.Reply.TrySetResult(allow);
        }
    }

    private async Task<bool> RequestPermissionAsync(ActiveRun run, AutomationPermission request, CancellationToken token)
    {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_permissions.ContainsKey(run.Record.RunId)) throw new InvalidOperationException("已有工具请求正在等待处理");
            _permissions[run.Record.RunId] = (run.Task.Id, request, reply);
        }
        try
        {
            Changed?.Invoke();
            _notify?.Invoke($"{run.Task.DisplayName} 等待工具授权", "请在自动化运行记录中允许本次操作或拒绝", true);
            return await reply.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _permissions.Remove(run.Record.RunId);
            Changed?.Invoke();
        }
    }
}
