using System.Text.Json;
using Heron.ClientFramework.Plugins;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md")]
[Anchor("Docs/client-framework.md")]
public sealed class PluginEditionPlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heron-edition-tests-" + Guid.NewGuid().ToString("N"));
    public PluginEditionPlatformTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private PluginEdition Read(string json, string file = "edition.json")
    {
        var path = Path.Combine(_root, file);
        File.WriteAllText(path, json);
        return PluginEdition.Read(path);
    }
    private static PluginManifest Manifest(string id, params PluginDependency[] dependencies) => new()
    {
        Id = id, Version = "1.0.0", HostApi = "1", Modules = [new("Main", "main.dll", "Main")], Dependencies = dependencies
    };
    private PluginPackage Package(PluginManifest manifest)
    {
        var directory = Path.Combine(_root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "main.dll"), "fixture");
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return PluginPackage.Read(Path.Combine(directory, "plugin.json"));
    }

    [Fact]
    public void Legacy_strings_default_to_Windows_including_external_paths()
    {
        var edition = Read("""{"name":"legacy","plugins":["app"],"external_packages":["missing-package"]}""");
        Assert.Equal(new[] {"app"}, edition.ForPlatform("Win64").Plugins);
        Assert.Single(edition.ForPlatform("Win64").GetExternalPackageDirectories());
        Assert.Empty(edition.ForPlatform("Mac").Plugins);
        Assert.Empty(edition.ForPlatform("Mac").GetExternalPackageDirectories());
    }

    [Fact]
    public void Same_items_select_allow_deny_and_empty_allow_with_deny_winning()
    {
        var edition = Read("""
            {"name":"mixed","plugins":[
              {"id":"windows","platform_allow_list":["Win64"]},
              {"id":"shared","platform_allow_list":["Win64","Mac"]},
              {"id":"blocked","platform_allow_list":["Win64","Mac"],"platform_deny_list":["Mac"]},
              {"id":"none","platform_allow_list":[]}
            ]}
            """);
        Assert.Equal(new[] {"windows", "shared", "blocked"}, edition.ForPlatform("Win64").Plugins);
        Assert.Equal(new[] {"shared"}, edition.ForPlatform("Mac").Plugins);
        Assert.Empty(edition.ForPlatform("Linux").Plugins);
    }

    [Fact]
    public void External_location_is_platform_independent_and_edition_relative()
    {
        var edition = ExternalEdition();
        Assert.Contains("app", edition.ExternalPluginIds);
        Assert.Equal(Path.Combine(_root, "outside", "app"), Assert.Single(edition.ForPlatform("Mac").GetExternalPackageDirectories()));
        Assert.Equal(edition.ForPlatform("Win64").GetExternalPackageDirectories(), edition.ForPlatform("Mac").GetExternalPackageDirectories());
    }

    [Fact]
    public void Build_round_trip_preserves_external_locations_and_platform_selection()
    {
        var edition = ExternalEdition();
        var roundTrip = Read(edition.ToBuildJson(), "relocated.json");
        Assert.Equal(edition.PluginPackagePaths, roundTrip.PluginPackagePaths);
        Assert.Equal(edition.ForPlatform("Mac").Plugins, roundTrip.ForPlatform("Mac").Plugins);
    }

    [Fact]
    public void Runtime_round_trip_preserves_selection_without_developer_paths()
    {
        var selected = ExternalEdition().ForPlatform("Mac");
        var installed = Read(selected.ToJson(), "installed.json");

        Assert.Equal(selected.Plugins, installed.Plugins);
        Assert.Equal(selected.TargetPlatform, installed.TargetPlatform);
        Assert.Equal(selected.ExternalPluginIds, installed.ExternalPluginIds);
        Assert.Empty(installed.PluginPackagePaths);
        Assert.Empty(installed.ExternalPackages);
    }

    [Theory]
    [InlineData("{\"id\":\"app\",\"is_external_package\":true}")]
    [InlineData("{\"id\":\"app\",\"package_path\":\"somewhere\"}")]
    [InlineData("{\"id\":\"app\",\"platform_allow_list\":[\"OSX\"]}")]
    [InlineData("{\"id\":\"app\",\"platform_allow_list\":[\"Mac\",\"mac\"]}")]
    [InlineData("{\"id\":\"app\",\"platforms\":[\"Mac\"]}")]
    public void Invalid_items_fail_clearly(string item) =>
        Assert.Throws<InvalidDataException>(() => Read("{\"name\":\"invalid\",\"plugins\":[" + item + "]}"));

    [Fact]
    public void Filtered_required_dependency_reports_parent_dependency_and_target()
    {
        var edition = Read("""
            {"name":"deps","plugins":[
              {"id":"app","platform_allow_list":["Mac"]},
              {"id":"service","platform_allow_list":["Win64"]}
            ]}
            """).ForPlatform("Mac");
        var dependency = Manifest("service");
        var consumer = Manifest("app", new PluginDependency(dependency.Id, dependency.Version));
        var error = Assert.Throws<InvalidDataException>(() => edition.Resolve([consumer, dependency], "1"));
        Assert.Contains(edition.TargetPlatform!, error.Message);
        Assert.Contains(consumer.Id, error.Message);
        Assert.Contains(dependency.Id, error.Message);
    }

    [Fact]
    public void Optional_filtered_dependency_is_not_pulled_back_in()
    {
        var edition = Read("""{"name":"optional","plugins":[{"id":"app","platform_allow_list":["Mac"]},{"id":"service","platform_allow_list":["Win64"]}]}""").ForPlatform("Mac");
        Assert.Equal(new[] {"app"}, edition.Resolve([Manifest("app", new PluginDependency("service", "1.0.0", true)), Manifest("service")], "1").LoadOrder.Select(m => m.Id));
    }

    [Theory]
    [InlineData("Win64")]
    [InlineData("Mac")]
    [InlineData("Linux")]
    public void Planning_debug_and_release_assembly_share_exact_selection(string platform)
    {
        var edition = Read("""{"name":"same","plugins":[{"id":"app","platform_allow_list":["Win64","Mac"]},"windows"]}""").ForPlatform(platform);
        var packages = new[] { Package(Manifest("app")), Package(Manifest("windows")) };
        var plan = PluginEditionLock.Resolve(edition, packages, "1");
        var debug = PluginEditionAssembler.Assemble(edition, packages, Path.Combine(_root, "Debug"));
        var release = PluginEditionPackaging.Copy(edition, packages, Path.Combine(_root, "Release"));
        Assert.Equal(edition.Plugins, plan.Plugins.Select(plugin => plugin.Id));
        Assert.Equal(plan.Plugins, debug.Plugins);
        Assert.Equal(plan.Plugins, release.Plugins);
        Assert.Equal(edition.Plugins, Read(File.ReadAllText(Path.Combine(_root, "Debug/edition.json")), "installed.json").Plugins);
    }

    private PluginEdition ExternalEdition() => Read("""
        {"name":"external","plugins":[{
          "id":"app","is_external_package":true,
          "package_path":"outside/app","platform_allow_list":["Win64","Mac"]
        }]}
        """);
}
