using Heron.Pregel.Automation;
using Heron.Pregel.Avalonia.ViewModels;
using Heron.Pregel.Avalonia.Views;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Runtime;
using Heron.ClientFramework.Shell;

namespace Heron.ClientHost;

/// <summary>Launcher model: starts the assembled plugins and mirrors their pages and statuses into the shell.</summary>
internal sealed partial class HostModel : ObservableObject, IAsyncDisposable
{
    private readonly HostEnvironment _environment = new();
    private readonly ClientPluginRegistry _registry = new();
    private readonly ClientPluginHostCore _core;
    private readonly bool _pluginsAssembled = File.Exists(Path.Combine(AppContext.BaseDirectory, "edition.json"));
    private readonly AutomationHost _automation;
    private AutomationPageViewModel? _automationPage;
    private AutomationPageView? _automationView;
    private IDisposable? _automationActivity;
    private const string AutomationPageKey = "heron.automation";
    private readonly Dictionary<string, PluginPageViewModel> _pages = new();

    public HostModel()
    {
        AutomationRuntime.DataRootProvider = () => Path.Combine(HostSettings.Root, "automation");
        _automation = new AutomationHost(new AutomationStore(AutomationRuntime.DataRoot),
            Path.Combine(AutomationRuntime.DataRoot, "evidence"), isActive: true);
        NavigationItems.Add(new NavigationItemViewModel
            { PageKey = AutomationPageKey, Title = "自动化", Icon = Icon.CalendarClock, Order = 0 });
        _core = new ClientPluginHostCore(new ClientPluginHostOptions
        {
            BaseDirectory = AppContext.BaseDirectory,
            PluginsRoot = Path.Combine(HostSettings.Root, "plugins"),
            HostAssemblyPath = typeof(HostModel).Assembly.Location,
            Log = text => Console.WriteLine("[host] " + text),
        }, _registry, manifest =>
        {
            var presentation = new PluginPresentation(manifest.Id, _registry, _environment, _environment);
            return new Dictionary<Type, object>
            {
                [typeof(IClientEnvironment)] = _environment,
                [typeof(IAutomationRegistry)] = _automation,
                [typeof(IClientAppPresence)] = presentation,
                [typeof(IClientPageNavigation)] = presentation,
            };
        });
        _registry.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        _environment.PageNavigationRequested += id =>
            SelectedNavigationItem = NavigationItems.FirstOrDefault(n => n.PageKey == id);
        var settings = HostSettings.Load();
        State = new ShellState(pinned: settings.SidebarPinned,
            persistPinned: pinned => HostSettings.Update(s => s.SidebarPinned = pinned));
        var name = Environment.UserName;
        User = new ShellUser
        {
            DisplayName = name, AccountName = name, Organization = Environment.MachineName, Description = name,
            Initials = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
        };
    }

    public ShellState State { get; }
    public ShellUser User { get; }
    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; } = new();
    public ObservableCollection<PluginPageViewModel> MountedPages { get; } = new();
    public IReadOnlyList<ClientPluginStatus> Statuses => _registry.Statuses.Where(s => s.LeftContent == null).ToArray();
    public IReadOnlyList<ClientPluginStatus> LeftStatuses => _registry.Statuses.Where(s => s.LeftContent != null).ToArray();

    [ObservableProperty] private NavigationItemViewModel? _selectedNavigationItem;
    [ObservableProperty] private object? _pageContent;
    [ObservableProperty] private string? _error;

    public async Task InitializeAsync()
    {
        if (_pluginsAssembled) await _core.InitializeAsync();
        await _automation.Scripts.RestoreAsync();
        foreach (var script in Program.Options.Scripts ?? [])
        {
            await _automation.Scripts.OpenDirectoryAsync(Path.GetDirectoryName(script)!);
            await _automation.Scripts.LoadAsync(script);
        }
        Error = _core.Error;
        Refresh();
    }

    public void MoveNavigationItem(object item, object target)
    {
        if (item is not NavigationItemViewModel from || target is not NavigationItemViewModel to) return;
        NavigationItems.Move(NavigationItems.IndexOf(from), NavigationItems.IndexOf(to));
    }

    partial void OnSelectedNavigationItemChanged(NavigationItemViewModel? value)
    {
        var active = value != null ? _pages.GetValueOrDefault(value.PageKey) : null;
        foreach (var page in _pages.Values) page.IsActive = ReferenceEquals(page, active);
        var automationActive = value?.PageKey == AutomationPageKey;
        if (automationActive)
        {
            _automationPage ??= new AutomationPageViewModel(_automation);
            if (_automationView == null)
            {
                _automationView = new AutomationPageView { DataContext = _automationPage };
                _automationActivity = NativePageActivity.Bind(_automationView);
            }
        }
        // Kept-alive pages draw in the page host; others are shown as the current content.
        PageContent = automationActive ? _automationView : active is { KeepMounted: false } ? active.View : null;
        State.ContentFollowsSidebar = active?.KeepMounted == true;
        _environment.NotifyCurrentPage(value?.PageKey);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Statuses));
        OnPropertyChanged(nameof(LeftStatuses));
        var visible = _registry.Pages;
        foreach (var id in _pages.Keys.Where(id => _registry.AllPages.All(p => p.Id != id)).ToArray())
        {
            MountedPages.Remove(_pages[id]);
            _pages[id].Dispose();
            _pages.Remove(id);
        }
        foreach (var page in _registry.AllPages.Where(p => !_pages.ContainsKey(p.Id)))
        {
            var model = new PluginPageViewModel(page);
            _pages.Add(page.Id, model);
            if (page.KeepMounted) MountedPages.Add(model);
        }
        foreach (var item in NavigationItems.Where(n => n.PageKey != AutomationPageKey && visible.All(p => p.Id != n.PageKey)).ToArray())
            NavigationItems.Remove(item);
        foreach (var page in visible.Where(p => NavigationItems.All(n => n.PageKey != p.Id)))
            NavigationItems.Add(new NavigationItemViewModel
            {
                PageKey = page.Id, Title = page.Title, Order = page.Order,
                Icon = Enum.TryParse<Icon>(page.Icon, out var icon) ? icon : Icon.Apps,
            });
        if (SelectedNavigationItem == null || !NavigationItems.Contains(SelectedNavigationItem))
            SelectedNavigationItem = NavigationItems.FirstOrDefault();
    }

    /// <summary>Prints what started and returns the process exit code for <c>--smoke</c>.</summary>
    public int ReportSmoke()
    {
        foreach (var status in _core.Status)
            Console.WriteLine($"[smoke] plugin {status.Id}@{status.Version} {status.State} {status.Error}");
        foreach (var page in _registry.Pages) Console.WriteLine($"[smoke] page {page.Id} {page.Title}");
        if (Error != null) Console.WriteLine("[smoke] error " + Error);
        Console.WriteLine("[smoke] automation " + _automation.IsActive);
        return Error == null && (!_pluginsAssembled || _core.Started) && _automation.IsActive ? 0 : 1;
    }

    public async ValueTask DisposeAsync()
    {
        _automationActivity?.Dispose();
        _automationPage?.Dispose();
        foreach (var page in _pages.Values) page.Dispose();
        State.Dispose();
        await _core.DisposeAsync();
        await _automation.DisposeAsync();
    }
}
