using FluentAssertions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Shell;
using Xunit;

namespace Heron.ClientFramework.Tests;

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
}
