using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.Runtime;

namespace Heron.ClientFramework.Shell;

/// <summary>View model for the shell's plugin toggle grid, decoupled from product-specific plugin filtering.</summary>
public partial class PluginsPageViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PluginStatus>> _getStatus;
    private readonly Func<string?> _getError;
    private readonly Func<string?> _getRecoveryMessage;
    private readonly Func<string, Task> _enableAsync;
    private readonly Func<string, Task> _disableAsync;
    private readonly Func<PluginStatus, bool>? _filter;

    public PluginsPageViewModel(ClientPluginHostCore host, Func<PluginStatus, bool>? filter = null)
        : this(() => host.Status, () => host.Error, () => host.RecoveryMessage,
            host.EnableAsync, host.DisableAsync, filter)
    {
        ArgumentNullException.ThrowIfNull(host);
    }

    public PluginsPageViewModel(
        Func<IReadOnlyList<PluginStatus>> getStatus,
        Func<string?> getError,
        Func<string?> getRecoveryMessage,
        Func<string, Task> enableAsync,
        Func<string, Task> disableAsync,
        Func<PluginStatus, bool>? filter = null)
    {
        _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        _getError = getError ?? throw new ArgumentNullException(nameof(getError));
        _getRecoveryMessage = getRecoveryMessage ?? throw new ArgumentNullException(nameof(getRecoveryMessage));
        _enableAsync = enableAsync ?? throw new ArgumentNullException(nameof(enableAsync));
        _disableAsync = disableAsync ?? throw new ArgumentNullException(nameof(disableAsync));
        _filter = filter;
    }

    public ObservableCollection<PluginStatusRow> Plugins { get; } = [];

    [ObservableProperty] private string _message = "";

    [RelayCommand]
    public virtual void Refresh()
    {
        Plugins.Clear();
        foreach (var item in _getStatus())
        {
            if (_filter != null && !_filter(item)) continue;
            Plugins.Add(new(item, ChangeFeatureAsync));
        }
        Message = _getError() ?? _getRecoveryMessage()
            ?? "开关立即隐藏或恢复功能；正在运行的任务继续，关闭的插件重启后不再加载。";
    }

    private async Task ChangeFeatureAsync(string id, bool enabled)
    {
        try
        {
            if (enabled)
            {
                await _enableAsync(id);
                Refresh();
                Message = _getStatus().Any(item => item.Id == id && item.State != PluginState.Running)
                    ? "功能已设置开启，需要重启客户端恢复。" : "功能已开启。";
            }
            else
            {
                await _disableAsync(id);
                Refresh();
                Message = "功能入口已隐藏，后台任务继续运行；重启客户端后不再加载。";
            }
        }
        catch (Exception error)
        {
            Refresh();
            Message = enabled
                ? "开启功能失败：" + error.Message
                : "关闭功能失败：" + error.Message;
        }
    }
}

public sealed partial class PluginStatusRow : ObservableObject
{
    private readonly Func<string, bool, Task>? _change;
    private readonly PluginStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isEnabled;

    public IAsyncRelayCommand ToggleCommand { get; }

    public PluginStatusRow(PluginStatus status, Func<string, bool, Task>? change = null)
    {
        _status = status;
        _isEnabled = status.EnabledNextStart;
        _change = change;
        ToggleCommand = new AsyncRelayCommand(async () =>
        {
            var enabled = !IsEnabled;
            IsEnabled = enabled;
            if (_change != null) await _change(Id, enabled);
        });
    }

    public string Id => _status.Id;
    public string Name => string.IsNullOrWhiteSpace(_status.DisplayName) ? _status.Id : _status.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(_status.Description) ? "启用后可使用此插件提供的功能。" : _status.Description;
    public string DefaultText => _status.EnabledByDefault ? "默认开启" : "默认关闭";
    public string StateText => !IsEnabled ? "已关闭" : _status.State == PluginState.Running ? "已启用" : "重启后启用";
    public bool HasError => !string.IsNullOrWhiteSpace(_status.Error);
    public string Error => _status.Error ?? "";
}
