using System;
using Avalonia.Controls;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heron.ClientFramework.Shell;

public sealed partial class PluginPageViewModel(ClientPluginPage page) : ViewModelBase, IDisposable
{
    public string Id => page.Id;
    public bool KeepMounted => page.KeepMounted;
    private Control? _view;
    private IDisposable? _activity;
    public Control View
    {
        get
        {
            if (_view is not null) return _view;
            _view = page.CreateView();
            _activity = NativePageActivity.Bind(_view);
            Serilog.Log.ForContext<PluginPageViewModel>().Information(
                "Plugin page {PageId} created view={ViewType}", Id, _view.GetType().FullName);
            return _view;
        }
    }
    private void ReleaseInactiveView()
    {
        if (KeepMounted || _view is not { } view || page.TryReleaseView == null) return;
        if (!page.TryReleaseView(view)) return;
        _activity?.Dispose();
        _activity = null;
        _view = null;
    }
    public void Dispose()
    {
        ReleaseInactiveView();
        _activity?.Dispose();
        _activity = null;
    }
    public Control CreateView() => View;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostMaxWidth), nameof(HostMaxHeight), nameof(HostOpacity), nameof(HostZIndex))]
    private bool _isActive;
    partial void OnIsActiveChanged(bool value)
    {
        if (!value) ReleaseInactiveView();
    }
    public double HostMaxWidth => IsActive ? double.PositiveInfinity : 1;
    public double HostMaxHeight => IsActive ? double.PositiveInfinity : 50;
    public double HostOpacity => IsActive ? 1 : 0;
    public int HostZIndex => IsActive ? 1 : 0;
}
