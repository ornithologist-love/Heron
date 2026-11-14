using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Heron.ClientControls.Gallery;

/// <summary>
///     The Gallery workbench: grouped scenes, an independent preview window and, when it owns the
///     application, a light/dark picker. Hosted by the standalone window and by the Gallery plugin page.
/// </summary>
public sealed partial class GalleryView : UserControl
{
    private bool _ready;
    private bool _changingGroup;
    private string _scene = "components";

    public GalleryView() : this(ownsTheme: true) { }

    /// <param name="ownsTheme">False inside a host that controls the application theme; hides the picker.</param>
    public GalleryView(bool ownsTheme)
    {
        InitializeComponent();
        ThemePicker.IsVisible = ownsTheme;
        Groups.ItemsSource = GalleryCatalog.Scenes.Select(scene => scene.Group).Distinct()
            .Select(group => new TabItem { Header = group }).ToArray();
        Groups.SelectedIndex = 0;
        _ready = true;
        LoadGroup();
        DetachedFromVisualTree += (_, _) =>
        {
            DisposeContent(PreviewContent.Content);
            PreviewContent.Content = null;
        };
        AttachedToVisualTree += (_, _) => { if (PreviewContent.Content == null) LoadScene(); };
    }

    private void ChangeGroup(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || args.Source != Groups) return;
        LoadGroup();
    }

    private void LoadGroup()
    {
        if (Groups.SelectedItem is not TabItem group) return;
        var scenes = GalleryCatalog.Scenes.Where(scene => scene.Group == (string)group.Header!)
            .Select(scene => new TabItem { Header = scene.Title, Tag = scene.Id }).ToArray();
        _changingGroup = true;
        try
        {
            Scenes.SelectedItem = null;
            Scenes.ItemsSource = scenes;
            Scenes.SelectedItem = scenes[0];
            _scene = (string)scenes[0].Tag!;
            LoadScene();
        }
        finally
        {
            _changingGroup = false;
        }
    }

    private void ChangeScene(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || _changingGroup || args.Source != Scenes || Scenes.SelectedItem is not TabItem scene) return;
        _scene = (string)scene.Tag!;
        LoadScene();
    }

    private void ChangeTheme(object? sender, SelectionChangedEventArgs args)
    {
        if (!_ready || args.Source != ThemePicker) return;
        Application.Current!.RequestedThemeVariant = ThemePicker.SelectedIndex == 1 ? ThemeVariant.Dark : ThemeVariant.Light;
        LoadScene();
    }

    private void LoadScene()
    {
        DisposeContent(PreviewContent.Content);
        PreviewContent.Content = GalleryCatalog.Create(_scene);
    }

    private void OpenPreviewWindow(object? sender, RoutedEventArgs args)
    {
        var content = GalleryCatalog.Create(_scene);
        var window = new Window { Title = "控件独立预览", Width = 900, Height = 700, Content = content };
        window.Closed += (_, _) => DisposeContent(content);
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    private static void DisposeContent(object? content)
    {
        if (content is IDisposable disposable) disposable.Dispose();
    }
}
