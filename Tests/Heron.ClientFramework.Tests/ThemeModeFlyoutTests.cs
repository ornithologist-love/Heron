using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Heron.ClientControls.Chrome;
using Semi.Avalonia;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Collection(nameof(ShellWindowTests))]
public sealed class ThemeModeFlyoutTests
{
    [Fact]
    [Anchor("Docs/client-controls.md")]
    public void OpeningThemeMenuShowsAllChoicesAndEmitsTheSelectedMode()
    {
        HeadlessAppSupport.EnsureInitialized();
        var menu = new ThemeModeFlyout { SystemLabel = "Auto", LightLabel = "Day", DarkLabel = "Night" };
        var button = new TitleBarButton { Content = "Theme", Flyout = menu };
        var window = new Window { Width = 400, Height = 300, Content = button };
        var selected = new List<string>();
        menu.ModeSelected += (_, mode) => selected.Add(mode);
        try
        {
            window.Show();
            button.Focus();
            button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Dispatcher.UIThread.RunJobs();

            menu.IsOpen.Should().BeTrue();
            menu.Items.Should().HaveCount(3);
            var presenter = (MenuFlyoutPresenter)menu.Popup.Child!;
            presenter.Items.Should().HaveCount(3);
            presenter.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(["Auto", "Day", "Night"]);

            var day = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Day"));
            day.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            menu.Mode.Should().Be("light");
            selected.Should().Equal("light");

            menu.Hide();
            menu.LightLabel = "Daytime";
            menu.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            presenter.Items.Should().HaveCount(3);
            presenter.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                .Should().Contain(["Auto", "Daytime", "Night"]);
            var night = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Night"));
            night.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            menu.Mode.Should().Be("dark");
            selected.Should().Equal("light", "dark");
        }
        finally { menu.Hide(); window.Close(); }
    }
}
