using System.Collections.Generic;
using System.Linq;

namespace Heron.Pregel.Avalonia.ViewModels;

public sealed record AutomationLaunchParameterRow(string Key, string Label, string Value);

public sealed partial class AutomationRunItemViewModel
{
    public string LaunchCloudTaskName => Record.Launch?.TaskName ?? "未保存云端关联";
    public string LaunchCloudTaskId => Record.Launch?.TaskId ?? "未保存云端关联";
    public string LaunchLocalNickname => Record.Launch?.LocalNickname is { Length: > 0 } name
        ? name : Record.TaskSnapshot?.Name ?? "未保存";
    public string LaunchMachineName => Record.Launch?.MachineName ?? "历史记录未保存执行机器";
    public string LaunchScriptRevision => Record.Launch?.ScriptRevision is { Length: > 0 } revision ? revision : "未保存";
    public string LaunchTemplateId => Record.TaskSnapshot?.TemplateId ?? "未保存";
    public string LaunchSnapshotNotice => Record.Launch != null
        ? "启动时已冻结本次参数，包含当时采用的默认值与全局设置。修改任务设置不会改变这份记录。"
        : Record.TaskSnapshot != null
            ? "这条记录仅保存了当时的参数，缺少完整默认值快照。"
            : "这条历史记录未保存启动参数。";
    public string LaunchRenderedParameters => Record.Launch?.RenderedParameters ?? "";
    public bool HasLaunchRenderedParameters => LaunchRenderedParameters.Length > 0;
    public IReadOnlyList<AutomationLaunchParameterRow> LaunchParameterRows
    {
        get
        {
            var values = Record.Launch?.Parameters ?? Record.TaskSnapshot?.Parameters;
            if (values == null) return [];
            var fields = Record.Launch?.Fields.ToDictionary(field => field.Key);
            var keys = (Record.Launch?.Fields.Select(field => field.Key) ?? [])
                .Concat(values.Keys).Distinct().Where(values.ContainsKey);
            return keys.Select(key => new AutomationLaunchParameterRow(key,
                fields?.GetValueOrDefault(key)?.Label ?? key, values[key])).ToArray();
        }
    }
    private void NotifyLaunchParameters()
    {
        OnPropertyChanged(nameof(LaunchCloudTaskName));
        OnPropertyChanged(nameof(LaunchCloudTaskId));
        OnPropertyChanged(nameof(LaunchLocalNickname));
        OnPropertyChanged(nameof(LaunchMachineName));
        OnPropertyChanged(nameof(LaunchScriptRevision));
        OnPropertyChanged(nameof(LaunchTemplateId));
        OnPropertyChanged(nameof(LaunchSnapshotNotice));
        OnPropertyChanged(nameof(LaunchRenderedParameters));
        OnPropertyChanged(nameof(HasLaunchRenderedParameters));
        OnPropertyChanged(nameof(LaunchParameterRows));
    }
}
