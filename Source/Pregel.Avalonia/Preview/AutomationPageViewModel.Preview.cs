using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using Heron.AutomationTenants.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;


namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    // For read-only design previews. The Gallery host is inactive and never registers executable templates.
    public void SetLayoutPreview(IAutomationTemplate template, IReadOnlyList<AutomationTaskView> rows,
        AutomationRunRecord record, bool showError)
    {
        SelectedTask = null;
        Tasks.Clear();
        Templates.Clear();
        Templates.Add(new AutomationTemplateOption(template));
        foreach (var row in rows)
        {
            var item = new AutomationTaskItemViewModel(row.Task.Id);
            item.Apply(row);
            Tasks.Add(item);
        }
        RefreshTaskList();
        RefreshTemplateList();
        SelectedTask = Tasks[0];
        Editor = new AutomationEditorViewModel(template, rows[0].Task, _ => { }, () => { });
        Editor.SetCloudTask(new Heron.AutomationTenants.RemoteAutomationTaskDefinition
            { Id = "preview.workflow.environment", Name = "构建机环境初始化" });
        if (showError)
            Editor.ShowViolations([AutomationRuleViolation.Error(AutomationRules.ParametersInvalid,
                "工作区参数已失效。请选择可用的工作区；这是一条仅用于观察错误提示换行的预览信息。")]);
        Runs.Clear();
        Runs.Add(new AutomationRunItemViewModel(record));
        SelectedRun = Runs[0];
        IsSettingsExpanded = showError;
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasRuns));
        NotifySettingsLayout();
        NotifySelection();
    }

    public void SetLayoutPreviewScripts(string state)
    {
        ScriptDirectories.Clear();
        ScriptDirectories.Add(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeronPreview", "Scripts"));
        // Selecting a production directory starts a scan. Seed it before the view binds instead.
#pragma warning disable MVVMTK0034
        _selectedScriptDirectory = ScriptDirectories[0];
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedScriptDirectory));
        ScriptEntries.Clear();
        if (state != "empty")
            for (var i = 0; i < (state == "long" ? 18 : 4); i++)
                ScriptEntries.Add(new AutomationScriptEntry(System.IO.Path.Combine(ScriptDirectories[0], $"Workflow-{i + 1}.csx"),
                    $"Workflow-{i + 1}.csx", state == "long" ? $"每日构建与开发环境完整初始化工作流（长名称）-{i + 1}" : $"开发环境初始化-{i + 1}",
                    state == "error" && i == 1 ? "加载失败" : i == 0 ? "已加载" : "未加载", i == 0,
                    i == 0 ? "gallery.layout-preview" : null, state == "error" && i == 1 ? "脚本加载失败：预览依赖不可用。" : null)
                { Description = "检查环境、构建并归档结果。", SourceInfo = "预览目录 · 不加载或执行脚本" });
        FilterScriptEntries();
        ScriptMessage = state == "error" ? "脚本加载失败：请检查依赖和入口定义。" : "";
    }
}

public sealed partial class AutomationLogsViewModel
{
    public void SetLayoutPreview(string state)
    {
        Stop();
        _record = null;
        Scopes.Clear();
        Scopes.Add(new("", "全部日志 · 预览", []));
        SelectedScope = Scopes[0];
        var count = state == "empty" ? 0 : state == "long" ? 180 : 24;
        Document.Text = string.Join("\n", Enumerable.Range(1, count).Select(i =>
            $"2026-10-11 10:00:{i % 60:00} [{(state == "error" && i == 12 ? "错误" : "信息")}] 开发环境初始化 · 第 {i} 条预览日志"));
        _firstLine = count == 0 ? 0 : 1;
        _lastLine = _total = count;
        Notice = state == "error" ? "读取日志失败：这是一条用于查看错误样式的预览提示。" : "";
        NotifyRange();
    }
}

public sealed partial class AutomationArtifactsViewModel
{
    public void SetLayoutPreview(string state)
    {
        Files.Clear();
        if (state != "empty")
            for (var i = 0; i < (state == "long" ? 18 : 3); i++)
                Files.Add(new AutomationArtifactItem(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeronPreview", "Artifacts", $"Report-{i + 1}.md"),
                    state == "long" ? $"Development_Environment_Initialization_VeryLongReportName_{i + 1}.md" : $"Report-{i + 1}.md", 12800)
                {
                    Kind = "report", Sha256 = new string('a', 64),
                    Deliveries = [new("本地归档", "succeeded", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeronPreview", "Artifacts", $"Report-{i + 1}.md")),
                        new("示例交付目标", state == "error" ? "failed" : "succeeded", "/reports/pc/preview",
                            state == "error" ? "上传未完成，本地归档仍然保留。" : null)]
                });
        // The normal selection hook reads the file; preview content stays entirely in memory.
#pragma warning disable MVVMTK0034
        _selectedFile = Files.FirstOrDefault();
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedFile));
        HasArchive = state != "empty";
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeronPreview", "Artifacts");
        PreviewText = state == "empty" ? "" : "# 开发环境初始化报告\n\n检查完成，结果仅用于样式预览。\n\n| 工具 | 状态 |\n| --- | --- |\n| Git | 已就绪 |\n| Node.js | 已就绪 |\n\n## 运行记录\n\n" +
            string.Join("\n", Enumerable.Range(1, state == "long" ? 60 : 4).Select(i => $"- 第 {i} 步完成：检查工作区与构建依赖。"));
        IsMarkdown = state != "empty";
        Message = state == "error" ? "无法读取部分制品：预览错误信息。" : state == "empty" ? "本次运行尚未登记实际制品。" : "";
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasMultipleFiles));
    }
}

public sealed partial class AutomationPageViewModel
{
    /// <summary>A failed source run and a run continued from it, with reused, retried and pending steps.</summary>
    public void SetLayoutPreviewReexecution(string state)
    {
        var at = new DateTimeOffset(2026, 10, 22, 14, 3, 0, TimeSpan.FromHours(8));
        AutomationStepRecord Step(string key, string title, AutomationStepState stepState, string[] dependsOn) => new()
        {
            Key = key, Title = title, NodeIds = [key], DependsOn = dependsOn, State = stepState
        };
        var source = new AutomationRunRecord
        {
            RunId = "preview-source", TaskId = "preview-task-0", Trigger = "manual", QueuedAt = at, StartedAt = at,
            EndedAt = at.AddMinutes(3), Status = AutomationRunStatus.Failed, Summary = "「校验包体」失败：文件被占用",
            TaskSnapshot = new AutomationTask { Id = "preview-task-0", TemplateId = "gallery.layout-preview" },
            Steps =
            [
                Step("resolve", "解析参数", AutomationStepState.Succeeded, []) with { StartedAt = at, EndedAt = at.AddSeconds(1) },
                Step("pull", "拉取 Dev 包", AutomationStepState.Succeeded, ["resolve"]) with { StartedAt = at, EndedAt = at.AddMinutes(2) },
                Step("check", "校验包体", AutomationStepState.Failed, ["pull"]) with
                    { StartedAt = at.AddMinutes(2), EndedAt = at.AddMinutes(3), Attempt = 3, Error = "文件被占用：Package.pak" },
                Step("launch", "启动 Client 预热", AutomationStepState.NotRun, ["check"]),
                Step("notify", "发送通知", AutomationStepState.NotRun, ["launch"])
            ]
        };
        var later = at.AddMinutes(67);
        var derived = source with
        {
            RunId = "preview-derived", QueuedAt = later, StartedAt = later, EndedAt = null, Status = AutomationRunStatus.Running,
            Summary = "运行中", SourceRunId = source.RunId, ReexecutionMode = "Continue",
            Steps =
            [
                Step("resolve", "解析参数", AutomationStepState.Succeeded, []) with { IsReused = true },
                Step("pull", "拉取 Dev 包", AutomationStepState.Succeeded, ["resolve"]) with { IsReused = true },
                Step("check", "校验包体", AutomationStepState.Succeeded, ["pull"]) with
                    { StartedAt = later, EndedAt = later.AddSeconds(18), Attempt = 2, RetryError = "文件被占用：Package.pak" },
                Step("launch", "启动 Client 预热", AutomationStepState.Running, ["check"]) with
                    { StartedAt = later.AddSeconds(18), Detail = "3 秒后第 2 次尝试", RetryError = "进程未就绪" },
                Step("notify", "发送通知", AutomationStepState.Pending, ["launch"])
            ]
        };
        Runs.Clear();
        Runs.Add(new AutomationRunItemViewModel(derived));
        Runs.Add(new AutomationRunItemViewModel(source));
        SelectedRun = state == "derived" ? Runs[0] : Runs[1];
        OnPropertyChanged(nameof(HasRuns));
        Reexecution.SetLayoutPreview(SelectedRun, Runs, state == "blocked"
            ? "此图已执行收尾，无法保证原资源仍有效。请从图入口重新运行。" : null);
    }
}
