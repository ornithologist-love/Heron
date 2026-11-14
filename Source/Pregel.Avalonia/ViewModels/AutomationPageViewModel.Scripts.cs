using Heron.Pregel.Automation;
using Heron.AutomationTenants;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace Heron.Pregel.Avalonia.ViewModels;

public sealed partial class AutomationPageViewModel
{
    public ObservableCollection<string> ScriptDirectories { get; } = [];
    public ObservableCollection<AutomationScriptEntry> ScriptEntries { get; } = [];
    public ObservableCollection<AutomationScriptEntry> VisibleScriptEntries { get; } = [];
    [ObservableProperty] private string _scriptSearchText = "";
    [ObservableProperty] private string? _selectedScriptDirectory;
    [ObservableProperty] private bool _showScriptBrowser;
    [ObservableProperty] private bool _isScriptBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasScriptMessage))] private string _scriptMessage = "";
    public bool HasScriptMessage => ScriptMessage.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTaskScriptMessage))] private string _taskScriptMessage = "";
    public bool HasTaskScriptMessage => TaskScriptMessage.Length > 0;
    public bool ShowTaskScriptReload => !IsSelectedTaskInvalid && SavedTask != null && SelectedScriptTemplateId is { } templateId
        && (_host.Scripts.LoadedScriptPath(templateId) ?? _host.Scripts.SavedScriptPath(templateId)) != null;
    public bool CanReloadSelectedScript => ShowTaskScriptReload && !IsScriptBusy && !HasUnsavedChanges
        && SavedTask is { IsActive: false, HasPendingReview: false };
    public string TaskScriptReloadText => IsScriptBusy ? "加载中…" : "重新加载";
    public string TaskScriptReloadTip => HasUnsavedChanges ? "请先保存或放弃参数修改，再重新加载脚本"
        : SavedTask is { IsActive: true } or { HasPendingReview: true } ? "请先完成运行或审核，再重新加载脚本"
        : "重新读取当前任务的 CSX 及其引用文件，刷新参数和流程；后续运行使用新加载的入口";
    public bool HasScriptEntries => ScriptEntries.Count > 0;
    public bool HasVisibleScriptEntries => VisibleScriptEntries.Count > 0;
    public string ScriptSummary => $"{VisibleScriptEntries.Count} / {ScriptEntries.Count} 个脚本 · 已加载 {ScriptEntries.Count(entry => entry.IsLoaded)} 个";
    public bool IsBundledScriptDirectory => SelectedScriptDirectory != null && string.Equals(
        Path.TrimEndingDirectorySeparator(SelectedScriptDirectory), AutomationRuntime.ScriptsDirectory, StringComparison.OrdinalIgnoreCase);
    public bool IsRemoteScriptDirectory => SelectedScriptDirectory == AutomationScriptCatalog.RemoteDirectory;
    public bool CanRemoveScriptDirectory => !IsScriptBusy && SelectedScriptDirectory != null
        && !IsRemoteScriptDirectory && !IsBundledScriptDirectory && !_host.Scripts.HasLoadedScripts(SelectedScriptDirectory);
    public string ScriptDirectoryHint => IsRemoteScriptDirectory
        ? "官方单向更新 · 默认下载到 " + AutomationRuntime.ScriptsDirectory + " · 下载时同步 Shared · " + _host.Scripts.RemoteSourceUrl
        : IsBundledScriptDirectory
        ? "远程脚本默认下载目录 · 启动时保留；个人脚本请存放在其他目录"
        : SelectedScriptDirectory != null && !Directory.Exists(SelectedScriptDirectory)
            ? "目录不存在 · 可移除登记；仍有已加载脚本时请先卸载，任务会保留"
            : "工作目录 · 移除登记前请先卸载已加载的脚本，源文件和任务会保留";
    public bool ShowTaskDetails => HasSelection;
    public bool ShowTaskEmpty => !HasSelection;

    private void InitializeScripts()
    {
        _host.Scripts.Changed += OnScriptsChanged;
        UpdateScriptDirectories();
    }
    private void OnScriptsChanged()
    {
        if (!_pageActive) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_pageActive) return;
            UpdateScriptDirectories();
            NotifyTaskScriptReload();
            if (ShowScriptBrowser && !IsScriptBusy) RefreshScriptsCommand.Execute(null);
        });
    }
    private void UpdateScriptDirectories()
    {
        var selected = SelectedScriptDirectory;
        var directories = _host.Scripts.Directories;
        if (!ScriptDirectories.SequenceEqual(directories))
        {
            ScriptDirectories.Clear();
            foreach (var directory in directories) ScriptDirectories.Add(directory);
        }
        SelectedScriptDirectory = ContainingScriptDirectory(selected) ?? directories.FirstOrDefault();
    }
    partial void OnSelectedScriptDirectoryChanged(string? value)
    {
        ClearScriptEntries();
        ScriptSearchText = "";
        OnPropertyChanged(nameof(IsBundledScriptDirectory));
        OnPropertyChanged(nameof(IsRemoteScriptDirectory));
        OnPropertyChanged(nameof(ScriptDirectoryHint));
        OnPropertyChanged(nameof(CanRemoveScriptDirectory));
        if (ShowScriptBrowser && !IsScriptBusy) RefreshScriptsCommand.Execute(null);
    }
    partial void OnScriptSearchTextChanged(string value) => FilterScriptEntries();
    partial void OnIsScriptBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveScriptDirectory));
        OnPropertyChanged(nameof(CanRunSelected));
        NotifyTaskScriptReload();
    }

    private void NotifyTaskScriptReload()
    {
        OnPropertyChanged(nameof(ShowTaskScriptReload));
        OnPropertyChanged(nameof(CanReloadSelectedScript));
        OnPropertyChanged(nameof(TaskScriptReloadText));
        OnPropertyChanged(nameof(TaskScriptReloadTip));
        ReloadSelectedScriptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanReloadSelectedScript))]
    private async Task ReloadSelectedScriptAsync()
    {
        if (!CanReloadSelectedScript || SavedTask == null || SelectedScriptTemplateId is not { } templateId) return;
        var taskId = SavedTask.Id;
        _scriptInspection?.Cancel();
        var path = _host.Scripts.LoadedScriptPath(templateId) ?? _host.Scripts.SavedScriptPath(templateId);
        if (path == null) return;
        IsScriptBusy = true;
        TaskScriptMessage = "正在重新加载脚本及引用文件…";
        try
        {
            if (_selectedScriptEntry is { IsRemote: true } entry && (entry.NeedsDownload || entry.NeedsReload))
                await _host.Scripts.DownloadAndLoadAsync(entry.Path);
            else await _host.Scripts.LoadAsync(path);
            _selectedScriptEntry = null;
            Refresh();
            if (SavedTask?.Id == taskId)
            {
                ShowPlanView();
                TaskScriptMessage = "已重新加载，参数和流程预览已刷新。";
            }
        }
        catch (Exception ex)
        {
            if (SavedTask?.Id == taskId) TaskScriptMessage = "重新加载失败：" + ex.Message;
        }
        finally { IsScriptBusy = false; }
    }
    partial void OnShowScriptBrowserChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTaskDetails));
        OnPropertyChanged(nameof(ShowTaskEmpty));
    }
    [RelayCommand]
    private async Task ToggleScriptBrowserAsync()
    {
        ShowScriptBrowser = !ShowScriptBrowser;
        if (ShowScriptBrowser) await RefreshScriptsAsync();
    }

    public Task OpenScriptDirectoryAsync(string directory) => ScriptActionAsync(async () =>
    {
        await _host.Scripts.OpenDirectoryAsync(directory);
        UpdateScriptDirectories();
        SelectedScriptDirectory = ContainingScriptDirectory(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory))));
        ShowScriptBrowser = true;
    });

    [RelayCommand] private Task RefreshScriptsAsync() => ScriptActionAsync(() => Task.CompletedTask);

    private string? ContainingScriptDirectory(string? directory) => directory == null ? null
        : ScriptDirectories.FirstOrDefault(root => directory.Equals(root, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private Task LoadScriptAsync(AutomationScriptEntry? entry) => ScriptActionAsync(async () =>
    {
        if (entry == null) return;
        if (entry.IsRemote) await _host.Scripts.DownloadAndLoadAsync(entry.Path);
        else await _host.Scripts.LoadAsync(entry.Path);
    });

    [RelayCommand]
    private Task CreateScriptTaskAsync(AutomationScriptEntry? entry) => ScriptActionAsync(async () =>
    {
        if (entry == null) return;
        var id = entry.IsRemote && (entry.NeedsDownload || entry.NeedsReload || !entry.IsLoaded)
            ? await _host.Scripts.DownloadAndLoadAsync(entry.Path)
            : entry.TemplateId ?? await _host.Scripts.LoadAsync(entry.Path);
        var template = _host.Templates.FirstOrDefault(t => t.Id == id)
            ?? throw new InvalidOperationException("脚本尚未加载");
        StartDraft(template, null, null);
        ShowScriptBrowser = false;
    });

    [RelayCommand]
    private Task UnloadScriptAsync(AutomationScriptEntry? entry) => ScriptActionAsync(async () =>
    {
        if (entry != null) await _host.Scripts.UnloadAsync(entry.Path);
    });

    [RelayCommand]
    private Task RemoveScriptDirectoryAsync() => ScriptActionAsync(async () =>
    {
        if (SelectedScriptDirectory is { } directory) await _host.Scripts.RemoveDirectoryAsync(directory);
        UpdateScriptDirectories();
    });

    private async Task ScriptActionAsync(Func<Task> action)
    {
        if (IsScriptBusy) return;
        IsScriptBusy = true;
        ScriptMessage = "";
        try
        {
            await action();
            await RefreshScriptEntriesAsync();
        }
        catch (Exception ex)
        {
            ScriptMessage = ex.Message;
            try { await RefreshScriptEntriesAsync(); } catch { /* Keep the original failure visible. */ }
        }
        finally { IsScriptBusy = false; }
    }

    private async Task RefreshScriptEntriesAsync()
    {
        ClearScriptEntries();
        while (true)
        {
            var directory = SelectedScriptDirectory;
            var entries = directory != null ? await _host.Scripts.ScanAsync(directory) : [];
            // Selection can change while discovery runs; never publish another directory's entries.
            if (!string.Equals(directory, SelectedScriptDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in entries) ScriptEntries.Add(entry);
            if (directory == AutomationScriptCatalog.RemoteDirectory && _host.Scripts.RemoteSourceError is { } error)
                ScriptMessage = error;
            break;
        }
        OnPropertyChanged(nameof(HasScriptEntries));
        OnPropertyChanged(nameof(CanRemoveScriptDirectory));
        OnPropertyChanged(nameof(ScriptDirectoryHint));
        FilterScriptEntries();
    }

    private void ClearScriptEntries()
    {
        ScriptEntries.Clear();
        OnPropertyChanged(nameof(HasScriptEntries));
        OnPropertyChanged(nameof(CanRemoveScriptDirectory));
        FilterScriptEntries();
    }

    private void FilterScriptEntries()
    {
        var search = ScriptSearchText.Trim();
        VisibleScriptEntries.Clear();
        foreach (var entry in ScriptEntries.Where(entry => search.Length == 0
            || entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)))
            VisibleScriptEntries.Add(entry);
        OnPropertyChanged(nameof(HasVisibleScriptEntries));
        OnPropertyChanged(nameof(ScriptSummary));
    }
}
