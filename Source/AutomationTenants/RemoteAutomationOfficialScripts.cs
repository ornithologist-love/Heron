using Heron.Pregel.Automation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Heron.AutomationTenants;

/// <summary>Official source discovery and selected-entry preparation; never executes a graph.</summary>
internal sealed class RemoteAutomationOfficialScripts(AutomationHost automation, RemoteAutomationEnvironment environment)
{
    private const string Prefix = "official.";
    private readonly object _gate = new();
    private AutomationOfficialScript[] _entries = [];
    private Task _refresh = Task.CompletedTask;
    private DateTimeOffset _nextRefresh;
    private readonly Dictionary<string, Task<Draft>> _preparations = [];

    internal sealed record Draft(AutomationTask Task, IAutomationTemplate Template, string Revision);

    public IReadOnlyList<AutomationOfficialScript> Entries => Volatile.Read(ref _entries);
    public static bool IsOfficial(string taskId) => taskId.StartsWith(Prefix, StringComparison.Ordinal);
    public bool IsPublished(string taskId) => IsOfficial(taskId) || Entries.Any(entry => TaskId(entry) == taskId);
    public static string TaskId(AutomationOfficialScript entry) => entry.TaskId ?? TaskId(entry.Id);
    public static string TaskId(string scriptId) => Prefix + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(scriptId.ToUpperInvariant())))[..24].ToLowerInvariant();
    private static string Revision(AutomationOfficialScript entry) => entry.Revision + ":shared:" + entry.SharedRevision;

    public void RefreshIfDue(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_refresh.IsCompleted || DateTimeOffset.UtcNow < _nextRefresh) return;
            _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
            // Catalog network IO must not delay the agent's mailbox, cancellation or run reports.
            _refresh = Task.Run(async () =>
            {
                try
                {
                    var entries = await new AutomationOfficialScripts().ListAsync(token);
                    Volatile.Write(ref _entries, entries.ToArray());
                    lock (_gate) _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    Serilog.Log.ForContext<RemoteAutomationOfficialScripts>().Warning(
                        "Official automation catalog refresh: {Message}", error.Message);
                }
            });
        }
    }

    public string? Conflict(AutomationTask task)
    {
        if (!IsPublished(task.Id)) return null;
        var path = automation.Scripts.SavedScriptPath(task.TemplateId);
        if (path == null) return "官方流程标识与本机任务冲突，未修改原任务。";
        var relative = Path.GetRelativePath(environment.ScriptsRoot(), path);
        var suite = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var entry = Entries.FirstOrDefault(candidate => TaskId(candidate) == task.Id);
        if (Path.IsPathRooted(relative) || suite is "." or ".." || (entry == null ? TaskId(suite) != task.Id : suite != entry.Id)
            || entry != null && !SameEntry(path, entry.Path))
            return "官方流程标识与本机任务脚本冲突，未修改原任务。";
        return null;
    }

    public Task<Draft> Prepare(string taskId, CancellationToken token, bool reload = false)
    {
        var saved = automation.Tasks.FirstOrDefault(view => view.Task.Id == taskId);
        if (!IsPublished(taskId) && saved != null)
            return PrepareSaved(saved.Task, token, reload);
        if (!IsPublished(taskId)) throw new InvalidOperationException("本机任务不存在。");
        RefreshIfDue(token);
        lock (_gate)
        {
            if (_preparations.TryGetValue(taskId, out var existing))
            {
                if (!existing.IsCompleted) return existing;
                if (!existing.IsCompletedSuccessfully)
                {
                    // Deliver this failure once; a later explicit inspection/run may retry preparation.
                    _preparations.Remove(taskId);
                    return existing;
                }
                var entry = Entries.FirstOrDefault(candidate => TaskId(candidate) == taskId);
                if (!reload && entry != null && Revision(entry) == existing.Result.Revision
                    && IsCurrent(existing.Result.Template)
                    && automation.Templates.Any(template => ReferenceEquals(template, existing.Result.Template)))
                    return existing;
                _preparations.Remove(taskId);
            }
            var refresh = _refresh;
            var preparation = Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                if (!Entries.Any(candidate => TaskId(candidate) == taskId))
                    await refresh.WaitAsync(deadline.Token);
                var entry = Entries.FirstOrDefault(candidate => TaskId(candidate) == taskId)
                    ?? await PublishedEntryAsync(taskId, deadline.Token)
                    ?? throw new InvalidOperationException("官方目录中找不到此流程，请刷新目录后重试。");
                using var admission = new AutomationStore(AutomationStore.DefaultRoot).TryAcquireExecution()
                    ?? throw new InvalidOperationException("构建机正在维护或安装更新，请稍后重试。");
                var templateId = await automation.Scripts.DownloadAndLoadAsync(entry.Path, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                var template = automation.Templates.Single(item => item.Id == templateId);
                var task = new AutomationTask
                {
                    Id = taskId, TemplateId = templateId, Name = entry.Title,
                    Parameters = template.Fields.Where(field => !field.IsReadOnly)
                        .ToDictionary(field => field.Key, field => field.DefaultValue),
                    ManualEnabled = true, ScheduleEnabled = false
                };
                return new Draft(task, template, Revision(entry));
            });
            // Observe failures even when the requesting browser leaves during preparation.
            _ = preparation.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _preparations[taskId] = preparation;
            return preparation;
        }
    }

    public async Task<Draft> PrepareDeploymentAsync(RemoteAutomationDeployment request, CancellationToken token)
    {
        lock (_gate) _nextRefresh = DateTimeOffset.MinValue;
        RefreshIfDue(token);
        Task refresh;
        lock (_gate) refresh = _refresh;
        await refresh.WaitAsync(token);
        var entry = Entries.FirstOrDefault(item => TaskId(item) == request.TaskId)
            ?? await PublishedEntryAsync(request.TaskId, token)
            ?? throw new InvalidOperationException("此流程已不在允许分发的官方目录中。");
        var source = request.Source;
        if (entry.Id != source.SourceId || entry.Revision != source.Revision || entry.SharedRevision != source.SharedRevision
            || !entry.ArchiveSha256.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase)
            || !entry.SharedArchiveSha256.Equals(source.SharedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("发布版本已变化；本次部署保留原版本指纹，请重新下发部署。");
        var saved = automation.Tasks.FirstOrDefault(view => view.Task.Id == request.TaskId);
        if (saved != null && Conflict(saved.Task) is { } conflict)
            throw new InvalidOperationException(conflict);
        var prepared = await Prepare(request.TaskId, token, reload: true).WaitAsync(token);
        var path = automation.Scripts.SavedScriptPath(prepared.Task.TemplateId)
            ?? throw new InvalidOperationException("部署后未找到可加载的流程入口。");
        VerifyDeploymentVersion(path, source);
        return prepared;
    }

    internal static void VerifyDeploymentVersion(string path, RemoteAutomationDeploymentSource source)
    {
        var actual = AutomationLocalSource.Describe(path);
        var stamp = $"{source.Revision}:{source.Sha256}:{source.SharedRevision}:{source.SharedSha256}";
        if (actual == null || !actual.BaselineKnown || actual.LocalModified || actual.SourceId != source.SourceId
            || actual.BaseRevision != source.Revision || actual.SharedBaseRevision != source.SharedRevision
            || !AutomationRemoteSource.ReadInstalledStamp(path).Equals(stamp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机发布包或 Shared 未通过指定版本校验，拒绝部署就绪；请重新下载。");
    }

    private Task<Draft> PrepareSaved(AutomationTask saved, CancellationToken token, bool reload)
    {
        var path = automation.Scripts.SavedScriptPath(saved.TemplateId)
            ?? throw new InvalidOperationException("本机任务没有绑定可加载的脚本。");
        lock (_gate)
        {
            if (_preparations.TryGetValue(saved.Id, out var existing))
            {
                if (!existing.IsCompleted) return existing;
                _preparations.Remove(saved.Id);
                if (!existing.IsCompletedSuccessfully || !reload && IsCurrent(existing.Result.Template) && automation.Templates.Any(
                        template => ReferenceEquals(template, existing.Result.Template))) return existing;
            }
            var preparation = Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                using var admission = new AutomationStore(AutomationStore.DefaultRoot).TryAcquireExecution()
                    ?? throw new InvalidOperationException("构建机正在维护或安装更新，请稍后重试。");
                // Reload the bound entry without replacing its saved ID, parameters or triggers.
                var templateId = await automation.Scripts.LoadAsync(path, deadline.Token);
                var current = automation.Tasks.FirstOrDefault(view => view.Task.Id == saved.Id)
                    ?? throw new InvalidOperationException("本机任务已被移除。");
                if (templateId != current.Task.TemplateId)
                    throw new InvalidOperationException("本机任务绑定已变化，请重新读取。");
                var template = automation.Templates.Single(item => item.Id == templateId);
                return new Draft(current.Task, template, automation.Scripts.SavedScriptRevision(templateId));
            });
            _ = preparation.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _preparations[saved.Id] = preparation;
            return preparation;
        }
    }

    public async Task FinishAsync()
    {
        Task[] work;
        lock (_gate) work = _preparations.Values.Cast<Task>().Append(_refresh).ToArray();
        try { await Task.WhenAll(work); }
        catch (Exception) { } // Each operation is reported to its caller or logged by the catalog refresh.
    }

    private static bool SameEntry(string left, string right)
    {
        static string Normalize(string path) => Path.GetFullPath(path).EndsWith(".Template.csx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)[..^".Template.csx".Length] + ".csx" : Path.GetFullPath(path);
        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrent(IAutomationTemplate template)
    {
        try { return template is not AutomationScriptTemplate script || !script.SourceChanged; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return false; }
    }

    private async Task<AutomationOfficialScript?> PublishedEntryAsync(string taskId, CancellationToken token)
    {
        var source = new AutomationOfficialScripts();
        var context = await environment.QueryPublishedSource(new Uri(source.SourceUrl).GetLeftPart(UriPartial.Authority), taskId, token);
        if (string.IsNullOrEmpty(context.Revision)) return null;
        var entries = await source.ListAsync(token, context.SourceId);
        var entry = entries.FirstOrDefault(item => TaskId(item) == taskId);
        if (entry == null) return null;
        if (entry.Revision != context.Revision)
            throw new InvalidOperationException("查询期间审核发布版已变化，请重新读取流程。");
        lock (_gate)
            Volatile.Write(ref _entries, Entries.Where(item => TaskId(item) != taskId).Append(entry).ToArray());
        return entry;
    }
}
