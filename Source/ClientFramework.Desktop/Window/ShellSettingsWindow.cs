using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Secondary hide-on-close settings window with a 40px self-drawn title bar matching <see cref="ShellWindow" />.
///     Closing hides the window until <see cref="CloseForExit" /> is called or the host exit predicate returns true.
/// </summary>
public class ShellSettingsWindow : Window
{
    private readonly Func<bool> _isExiting;
    private readonly ContentControl _bodyHost = new();
    private bool _closingForExit;

    public ShellSettingsWindow(
        string windowTitle = "设置",
        string headerTitle = "设置",
        Control? body = null,
        Func<bool>? isExiting = null,
        IImage? titleIcon = null,
        WindowIcon? windowIcon = null)
    {
        _isExiting = isExiting ?? (static () => false);
        Title = windowTitle;
        Width = 900;
        Height = 700;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = windowIcon ?? ShellBranding.WindowIcon;
        this.Bind(BackgroundProperty, new DynamicResourceExtension("BgAppBrush"));
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = 40;
        WindowDecorationsTheme = ShellWindow.CreateDecorationsTheme();

        _bodyHost.Content = body;
        Content = BuildLayout(headerTitle, titleIcon ?? ShellBranding.TitleIcon);
    }

    public void SetBody(Control? body) => _bodyHost.Content = body;

    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
        DataContext = null;
        Content = null;
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

    private Control BuildLayout(string title, IImage? titleIcon)
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

        Grid.SetRow(_bodyHost, 1);
        root.Children.Add(_bodyHost);
        return root;
    }
}
