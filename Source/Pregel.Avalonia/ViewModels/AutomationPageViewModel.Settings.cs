using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

/// <summary>参数页导航、校验定位和旧设置入口的适配；页面不再使用独立设置浮层。</summary>
public sealed partial class AutomationPageViewModel
{
    /// <summary>低于此宽度（整个页面可用宽度）时，展开的设置改为浮在图上方的面板。</summary>
    public const double NarrowWidth = 1280;

    public const string GroupTask = "task";
    public const string GroupSchedule = "schedule";
    public const string GroupOptions = "options";


    /// <summary>设置栏当前是否展开。只由下面的规则和用户操作改变。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsRail), nameof(ShowSettingsDocked), nameof(ShowSettingsOverlay),
        nameof(SettingsColumnWidth), nameof(SettingsSpacingWidth), nameof(SettingsToggleTip), nameof(ShowEditor),
        nameof(IsSettingsCollapsed))]
    private bool _isSettingsExpanded = true;

    /// <summary>页面可用宽度低于 <see cref="NarrowWidth"/>：由视图根据实际宽度设置。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsDocked), nameof(ShowSettingsOverlay), nameof(ShowSettingsRail),
        nameof(SettingsColumnWidth), nameof(SettingsSpacingWidth), nameof(ShowEditor))]
    private bool _isNarrowLayout;

    /// <summary>“设置收在这里”的一次性提示正在显示。</summary>
    [ObservableProperty] private bool _showSettingsHint;

    /// <summary>需要滚动定位到的分组；视图处理后清空。</summary>
    [ObservableProperty] private string? _settingsFocusGroup;

    /// <summary>审核视图下，设置只在用户点“查看任务设置”时出现（沿用原规则）。</summary>
    private bool SettingsAllowed => HasEditor && (!HasSelectedReview || ShowReviewSettings);

    /// <summary>展开的设置作为一栏停靠（宽窗口）。</summary>
    public bool ShowSettingsDocked => HasEditor && IsParametersTab;

    /// <summary>展开的设置作为浮层（窄窗口）。</summary>
    public bool ShowSettingsOverlay => false;

    /// <summary>收起时的图标栏；窄窗口展开为浮层时图标栏仍保留在原位。</summary>
    public bool ShowSettingsRail => false;

    public string SettingsToggleTip => IsSettingsExpanded ? "收起设置" : "展开设置";

    /// <summary>设置卡常驻在可视树里（收起时宽度为 0），这样展开 / 收起才有过渡动画。</summary>
    public bool ShowSettingsCard => SettingsAllowed;

    /// <summary>设置卡处于收起态（宽窗口收起，或窄窗口未打开浮层）。</summary>
    public bool IsSettingsCollapsed => !IsSettingsExpanded;

    /// <summary>草稿必须填完，不提供收起按钮。</summary>
    public bool CanCollapseSettings => Editor is { IsNew: false };

    // ---- 图标栏：每个分组的摘要与状态点 ----

    public string TaskGroupSummary => Editor == null ? ""
        : $"{(string.IsNullOrWhiteSpace(Editor.Name) ? Editor.TemplateTitle : Editor.Name.Trim())}";

    public string ScheduleGroupSummary => Editor == null ? ""
        : Editor.TriggerSummary;

    public string OptionsGroupSummary => Editor == null ? "" : BuildOptionsSummary(Editor);

    public bool TaskGroupHasError => GroupHasError(GroupTask);
    public bool ScheduleGroupHasError => GroupHasError(GroupSchedule);
    public bool OptionsGroupHasError => GroupHasError(GroupOptions);

    /// <summary>有未保存修改时，所有分组图标都标琥珀点（不区分具体分组，避免误导）。</summary>
    public bool SettingsHasUnsaved => HasUnsavedChanges && Editor is { IsNew: false };

    private bool GroupHasError(string group) =>
        Editor is { } editor && editor.Violations.Any(v => v.IsError && GroupOf(v.Violation) == group);

    /// <summary>规则 ID → 分组。未知的模板规则归到“参数”，那里是模板自己的字段。</summary>
    internal static string GroupOf(AutomationRuleViolation violation) => violation.RuleId switch
    {
        AutomationRules.NameRequired or AutomationRules.NameDuplicate
            or AutomationRules.TemplateMissing => GroupTask,
        AutomationRules.TimeInvalid or AutomationRules.TriggersInvalid or AutomationRules.TriggerDuplicate or AutomationRules.SameTimeSharedResource => GroupSchedule,
        _ => GroupOptions
    };

    private static string BuildOptionsSummary(AutomationEditorViewModel editor)
    {
        var toggles = editor.Fields.Where(f => f.IsToggle).ToArray();
        var on = toggles.Where(f => f.BoolValue).Select(f => f.Label).ToArray();
        var parts = new List<string>();
        if (toggles.Length > 0)
            parts.Add(on.Length == 0 ? "全部关闭" : $"已开启 {on.Length}/{toggles.Length}：{string.Join("、", on.Take(3))}{(on.Length > 3 ? " 等" : "")}");
        var others = editor.Fields.Count(f => !f.IsToggle);
        if (others > 0) parts.Add($"另有 {others} 项参数");
        parts.Add(editor.NotifyOnSuccess ? "成功也通知" : "仅失败通知");
        parts.Add(editor.AllowParallel ? "允许并行" : "同任务串行");
        return string.Join(" · ", parts);
    }

    // ---- 用户操作 ----

    [RelayCommand]
    private void ToggleSettings()
    {
        if (!HasEditor) return;
        // 草稿必须填完才能用，不允许收起。
        if (IsSettingsExpanded && Editor is { IsNew: true }) return;
        SetSettingsExpanded(!IsSettingsExpanded, remember: true);
    }

    /// <summary>点图标栏上的分组：展开并滚动到该分组。</summary>
    [RelayCommand]
    private void OpenSettingsGroup(string? group)
    {
        if (!HasEditor) return;
        SetSettingsExpanded(true, remember: true);
        SettingsFocusGroup = group;
    }

    [RelayCommand]
    private void DismissSettingsHint() => ShowSettingsHint = false;

    /// <summary>窄窗口下点击浮层外部 / Esc：收起，但有未保存修改或草稿时不收。</summary>
    [RelayCommand]
    private void CloseSettingsOverlay()
    {
        if (!ShowSettingsOverlay || Editor is { IsNew: true }) return;
        SetSettingsExpanded(false, remember: false);
    }

    // ---- 规则 ----

    /// <summary>选中任务或编辑器换了之后，按规则 1、2、5、6 决定初始状态。</summary>
    private void ApplySettingsRuleOnSelect()
    {
        ShowSettingsHint = false;
        SettingsFocusGroup = null;
        SelectInitialPage();
    }

    /// <summary>保存尝试之后：失败按规则 2 展开定位；首次保存成功按规则 4 收起并提示一次。</summary>
    private void ApplySettingsRuleAfterSave(bool wasNew, bool saved)
    {
        if (saved) return;
        PageTabIndex = 0;
        if (Editor?.Violations.Any(v => v.IsError) == true) ExpandToFirstError();
    }

    /// <summary>手动执行：设置展开且没有未保存修改时，收起让出空间。</summary>
    private void ApplySettingsRuleOnRun()
    {
        PageTabIndex = 2;
    }

    private void ExpandToFirstError()
    {
        SetSettingsExpanded(true, remember: false);
        var first = Editor?.Violations.FirstOrDefault(v => v.IsError);
        if (first != null) SettingsFocusGroup = GroupOf(first.Violation);
    }

    private void SetSettingsExpanded(bool expanded, bool remember)
    {
        IsSettingsExpanded = expanded;
        if (expanded) PageTabIndex = 0;
        ShowSettingsHint = false;
        NotifySettingsLayout();
    }

    /// <summary>编辑器内容变化：刷新图标栏摘要与状态点。</summary>
    private void NotifySettingsSummary()
    {
        RefreshParameterSections();
        OnPropertyChanged(nameof(TaskGroupSummary));
        OnPropertyChanged(nameof(ScheduleGroupSummary));
        OnPropertyChanged(nameof(OptionsGroupSummary));
        OnPropertyChanged(nameof(TaskGroupHasError));
        OnPropertyChanged(nameof(ScheduleGroupHasError));
        OnPropertyChanged(nameof(OptionsGroupHasError));
        OnPropertyChanged(nameof(SettingsHasUnsaved));
    }

    private void NotifySettingsLayout()
    {
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowSettingsDocked));
        OnPropertyChanged(nameof(ShowSettingsOverlay));
        OnPropertyChanged(nameof(ShowSettingsRail));
        OnPropertyChanged(nameof(SettingsColumnWidth));
        OnPropertyChanged(nameof(SettingsSpacingWidth));
        OnPropertyChanged(nameof(SettingsToggleTip));
        OnPropertyChanged(nameof(ShowSettingsCard));
        OnPropertyChanged(nameof(IsSettingsCollapsed));
        OnPropertyChanged(nameof(CanCollapseSettings));
        NotifySettingsSummary();
    }

}
