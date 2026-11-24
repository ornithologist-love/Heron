using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAssertions;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Runtime;
using Heron.ClientFramework.Shell;
using Semi.Avalonia;
using Xunit;

namespace Heron.ClientFramework.Tests;

internal static class HeadlessAppSupport
{
    public static void EnsureInitialized()
    {
        if (Application.Current != null) return;
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
        Application.Current!.Styles.Add(new SemiTheme());
        Application.Current.Styles.Add(new StyleInclude(new Uri("avares://Heron.ClientControls/"))
        {
            Source = new Uri("avares://Heron.ClientControls/Themes/HeronTheme.axaml")
        });
    }
}

// ThemeService is process state; this class owns every test that changes it.
[Collection(nameof(ShellWindowTests))]
public sealed class ShellWindowTests
{
    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void SidebarIsExpandedWhilePinnedOrHoveredAndOnlyNativePagesFollowTheDrawer()
    {
        var persisted = new List<bool>();
        using var state = new ShellState(pinned: false, persistPinned: persisted.Add);

        state.IsSidebarExpanded.Should().BeFalse();
        state.SidebarWidth.Should().Be(ShellState.CollapsedSidebarWidth);

        state.IsSidebarPointerOver = true;
        state.IsSidebarExpanded.Should().BeTrue();
        state.SidebarWidth.Should().Be(ShellState.ExpandedSidebarWidth);
        state.SidebarContentMargin.Left.Should().Be(ShellState.CollapsedSidebarWidth, "a hovering drawer floats over ordinary pages");
        state.ContentFollowsSidebar = true;
        state.SidebarContentMargin.Left.Should().Be(ShellState.ExpandedSidebarWidth, "a native page cannot be overlapped");

        state.IsSidebarPointerOver = false;
        state.ToggleSidebarPinnedCommand.Execute(null);
        state.IsSidebarExpanded.Should().BeTrue();
        state.SidebarContentMargin.Left.Should().Be(ShellState.ExpandedSidebarWidth);
        persisted.Should().Equal(true);

        state.ToggleSidebarPinnedCommand.Execute(null);
        persisted.Should().Equal(true, false);
        state.IsSidebarExpanded.Should().BeFalse();
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void ThemeModeIsThreeStatePersistedAndLabelledByTheHost()
    {
        var saved = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "heron-shell-" + Guid.NewGuid().ToString("N"));
        ClientHostProfile.Configure("Test", "Test", "Test", root, root, saved.Add);
        ThemeService.Initialize("system", "dark");
        using var state = new ShellState(new ShellText { ThemeLight = "Day", ThemeDark = "Night" });

        state.ThemeMode.Should().Be("system");
        state.IsDarkTheme.Should().BeTrue("system mode follows the reported system theme");
        state.ThemeText.Should().Be("跟随系统");

        state.SelectTheme("light");
        state.ThemeText.Should().Be("Day");
        state.IsDarkTheme.Should().BeFalse();
        ThemeService.UpdateSystemTheme("dark");
        state.IsDarkTheme.Should().BeFalse("an explicit choice ignores system changes");

        state.SelectTheme("dark");
        state.ThemeText.Should().Be("Night");
        state.SelectTheme("unknown");
        state.ThemeMode.Should().Be("system");
        saved.Should().Equal("light", "dark", "system");
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void UserMenuKeepsHostOrderAndAddsTheThemeEntryOnlyWhenTheHostDidNotPlaceIt()
    {
        ShellMenuSlots.Compose(["settings", "hidden", "sign-out"], e => e == "theme", () => "theme")
            .Should().Equal("theme", "settings", "hidden", "sign-out");
        ShellMenuSlots.Compose(["startup", "theme", "settings"], e => e == "theme", () => "other")
            .Should().Equal("startup", "theme", "settings");
        ShellMenuSlots.Compose(Array.Empty<string>(), e => e == "theme", () => "theme")
            .Should().Equal("theme");
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void ShellWindowAppliesBuiltInDefaultAndOverriddenShellBranding()
    {
        HeadlessAppSupport.EnsureInitialized();
        ShellBranding.ResetToDefaults();
        try
        {
            var defaultWindow = new ShellWindow();
            defaultWindow.Icon.Should().NotBeNull();
            defaultWindow.TitleIcon.Should().NotBeNull();
            new ShellUser().Avatar.Should().BeSameAs(defaultWindow.TitleIcon);

            using var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var windowIcon = new WindowIcon(bitmap);
            ShellBranding.Configure(windowIcon, bitmap);

            var window = new ShellWindow();
            window.Icon.Should().BeSameAs(windowIcon);
            window.TitleIcon.Should().BeSameAs(bitmap);
            new ShellUser().Avatar.Should().BeSameAs(bitmap);

            using var overrideImage = new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            window.TitleIcon = overrideImage;
            window.TitleIcon.Should().BeSameAs(overrideImage);
        }
        finally
        {
            ShellBranding.ResetToDefaults();
        }
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public async Task DiagnosticsManagerDeduplicatesWindowsAndReleasesPanelsOnShutdown()
    {
        HeadlessAppSupport.EnsureInitialized();
        var owner = new ShellWindow();
        owner.Show();
        var source = new FakeDiagnosticsWindow();
        var manager = new ShellDiagnosticsManager();
        try
        {
            var first = manager.OpenOrShow("ai", source, owner);
            var second = manager.OpenOrShow("ai", source, owner);
            first.Should().BeSameAs(second);
            source.OpenCount.Should().Be(1);
            source.ClosedCount.Should().Be(0);

            await manager.CloseAndReleaseAllAsync();
            Dispatcher.UIThread.RunJobs();
            source.ClosedCount.Should().Be(1);
            manager.OpenKeys.Should().BeEmpty();
        }
        finally
        {
            owner.Close();
        }
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void RegistryImportVisibleFiltersHiddenOwners()
    {
        var registry = new ClientPluginRegistry();
        var first = new FakeDiagnosticsWindow();
        var second = new FakeDiagnosticsWindow();
        using var reg1 = registry.Export<IClientDiagnosticsWindow>("alpha", "1.0.0", first);
        using var reg2 = registry.Export<IClientDiagnosticsWindow>("beta", "1.0.0", second);

        registry.ImportVisible<IClientDiagnosticsWindow>().Select(x => x.Owner).Should().Equal("alpha", "beta");

        registry.SetHiddenOwners(["alpha"]);
        registry.ImportVisible<IClientDiagnosticsWindow>().Select(x => x.Owner).Should().Equal("beta");
    }

    private sealed class FakeDiagnosticsWindow : IClientDiagnosticsWindow
    {
        public string Title => "Test Diagnostics";
        public int OpenCount { get; private set; }
        public int ClosedCount { get; private set; }

        public IClientDiagnosticsPanel OpenPanel()
        {
            OpenCount++;
            return new FakePanel();
        }

        public ValueTask ClosePanelAsync(IClientDiagnosticsPanel panel, CancellationToken cancellationToken)
        {
            ClosedCount++;
            return ValueTask.CompletedTask;
        }

        private sealed class FakePanel : IClientDiagnosticsPanel
        {
            public Control View { get; } = new TextBlock { Text = "Panel" };
        }
    }
}
