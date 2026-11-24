using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Heron.ClientFramework.Abstractions;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Non-modal secondary window hosting a plugin's <see cref="IClientDiagnosticsWindow" /> panel
///     inside a self-drawn 40px title bar matching <see cref="ShellWindow" />.
/// </summary>
public sealed class ShellDiagnosticsWindow : Window
{
    private readonly IClientDiagnosticsWindow _source;
    private readonly IClientDiagnosticsPanel _panel;
    private readonly Func<bool> _isExiting;
    private Task? _release;
    private bool _closingForExit;

    public ShellDiagnosticsWindow(
        IClientDiagnosticsWindow source,
        Window owner,
        IImage? titleIcon = null,
        Func<bool>? isExiting = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);
        _source = source;
        _isExiting = isExiting ?? (static () => false);
        _panel = source.OpenPanel();

        Title = source.Title;
        Width = 1180;
        Height = 780;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = owner.Icon ?? ShellBranding.WindowIcon;
        Background = owner.Background;
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = 40;
        WindowDecorationsTheme = ShellWindow.CreateDecorationsTheme();

        Content = BuildLayout(source.Title, _panel.View, titleIcon ?? ShellBranding.TitleIcon);
        Opened += (_, _) => Serilog.Log.ForContext("SourceContext", "Window").Information("Diagnostics window opened title={Title}", source.Title);
    }

    public IClientDiagnosticsWindow Source => _source;

    public void HideForBackground() => Hide();

    public void Reshow(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsVisible) Show(owner);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    public Task ReleaseAsync()
    {
        if (_release != null) return _release;
        var panel = _panel;
        Content = null;
        return _release = ReleaseCoreAsync(panel);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closingForExit && !_isExiting())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ReleaseCoreAsync(IClientDiagnosticsPanel panel)
    {
        try { await _source.ClosePanelAsync(panel, CancellationToken.None); }
        catch (Exception error)
        {
            Serilog.Log.ForContext("SourceContext", "Window").Warning(error, "Diagnostics panel release failed");
        }
    }

    private Control BuildLayout(string title, Control panelView, IImage? titleIcon)
    {
        var root = new Grid { RowDefinitions = RowDefinitions.Parse("40,*") };
        root.Bind(Layoutable.MarginProperty, this.GetObservable(OffScreenMarginProperty));

        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
        header.Bind(Border.BackgroundProperty, new DynamicResourceExtension("NavBgBrush"));
        header.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("BrandDividerBrush"));
        Win32Properties.SetNonClientHitTestResult(header, Win32Properties.Win32HitTestValue.Caption);

        var headerRow = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(12, 0, 150, 0),
        };
        if (titleIcon != null)
        {
            var icon = new Image
            {
                Source = titleIcon,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(icon, 0);
            headerRow.Children.Add(icon);
        }

        var titleText = new TextBlock
        {
            Text = title,
            Classes = { "textBody" },
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Grid.SetColumn(titleText, 1);
        headerRow.Children.Add(titleText);

        header.Child = headerRow;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var host = new ContentControl { Content = panelView };
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        return root;
    }
}
