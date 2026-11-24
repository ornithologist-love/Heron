using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Heron.ClientFramework.Runtime;

namespace Heron.ClientFramework.Shell;

/// <summary>Configuration for <see cref="ClientTrayHost" /> supplied by the embedding product.</summary>
public sealed class ClientTrayOptions
{
    public required string Title { get; init; }
    public string? OpenLabel { get; init; }
    public string ExitLabel { get; init; } = "退出";
    public string? ToolTipText { get; init; }
    public WindowIcon? Icon { get; init; }
    public required Action OnOpen { get; init; }
    public required Func<Task> OnExit { get; init; }
    public Action<Exception>? OnError { get; init; }
    public ClientPluginRegistry? PluginRegistry { get; init; }
    public Action<NativeMenu>? CustomizeMenu { get; init; }
}

/// <summary>
///     Manages the desktop <see cref="TrayIcon" />, its Open/Exit entries, dynamic plugin commands
///     from <see cref="ClientPluginRegistry" />, and optional host menu customization.
/// </summary>
public sealed class ClientTrayHost : IDisposable
{
    private readonly Application _application;
    private readonly ClientPluginRegistry? _registry;
    private readonly Action _refreshCommands;
    private TrayIcon? _trayIcon;
    private int _disposed;

    public ClientTrayHost(Application application, ClientTrayOptions options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);
        _application = application;
        _registry = options.PluginRegistry;

        var openItem = new NativeMenuItem { Header = options.OpenLabel ?? $"打开{options.Title}" };
        openItem.Click += (_, _) => options.OnOpen();

        var exitItem = new NativeMenuItem { Header = options.ExitLabel };
        exitItem.Click += async (_, _) =>
        {
            Serilog.Log.Information("Exit requested from tray");
            try { await options.OnExit(); }
            catch (Exception error)
            {
                Serilog.Log.Error(error, "Exit failed");
                options.OnError?.Invoke(error);
            }
        };

        var separator = new NativeMenuItemSeparator();
        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(separator);
        menu.Items.Add(exitItem);

        var commandItems = new List<NativeMenuItem>();
        _refreshCommands = () =>
        {
            if (_registry == null) return;
            foreach (var item in commandItems) menu.Items.Remove(item);
            commandItems.Clear();
            foreach (var command in _registry.Commands)
            {
                var item = new NativeMenuItem { Header = command.Title };
                item.Click += async (_, _) =>
                {
                    try { await command.Execute(default); }
                    catch (Exception error) { options.OnError?.Invoke(error); }
                };
                commandItems.Add(item);
                var insertIndex = menu.Items.IndexOf(separator);
                menu.Items.Insert(insertIndex >= 0 ? insertIndex : Math.Max(0, menu.Items.Count - 2), item);
            }
        };

        if (_registry != null)
        {
            _registry.Changed += _refreshCommands;
            _refreshCommands();
        }

        options.CustomizeMenu?.Invoke(menu);

        _trayIcon = new TrayIcon
        {
            Icon = options.Icon ?? ShellBranding.WindowIcon,
            Menu = menu,
            ToolTipText = options.ToolTipText ?? $"{options.Title}（后台运行中）",
            Command = new RelayCommand(options.OnOpen),
        };
        TrayIcon.SetIcons(_application, new TrayIcons { _trayIcon });
    }

    public NativeMenu? Menu => _trayIcon?.Menu;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_registry != null) _registry.Changed -= _refreshCommands;
        _trayIcon?.Dispose();
        _trayIcon = null;
        TrayIcon.SetIcons(_application, null);
    }
}
