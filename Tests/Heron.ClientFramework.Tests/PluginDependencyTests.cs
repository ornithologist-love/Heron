using Heron.ClientFramework.Plugins;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md")]
public class PluginDependencyTests
{
    private static PluginManifest Plugin(string id, params PluginDependency[] dependencies) => new()
    {
        Id = id, Version = "1.0.0", HostApi = "1",
        Modules = [new("Main", "main.dll", "Main")], Dependencies = dependencies
    };

    [Fact]
    public void Required_closure_is_ordered_and_unselected_features_are_excluded()
    {
        var result = PluginDependencyGraph.Resolve([Plugin("app", new PluginDependency("service", ">=1.0.0 <2.0.0")), Plugin("service"), Plugin("unused")], ["app"], "1");
        Assert.Equal(["service", "app"], result.LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public void Optional_dependency_does_not_expand_edition_but_orders_selected_provider()
    {
        var catalog = new[] { Plugin("packages", new PluginDependency("mobile", "1.0.0", true)), Plugin("mobile") };
        Assert.Single(PluginDependencyGraph.Resolve(catalog, ["packages"], "1").LoadOrder);
        Assert.Equal(["mobile", "packages"], PluginDependencyGraph.Resolve(catalog, ["packages", "mobile"], "1").LoadOrder.Select(p => p.Id));
    }

    [Fact]
    public void Cycles_include_path()
    {
        var error = Assert.Throws<InvalidDataException>(() => PluginDependencyGraph.Resolve([Plugin("a", new PluginDependency("b", "1.0.0")), Plugin("b", new PluginDependency("a", "1.0.0"))], ["a"], "1"));
        Assert.Contains("a -> b -> a", error.Message);
    }

    [Fact]
    public void Incompatible_host_and_required_version_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => PluginDependencyGraph.Resolve([Plugin("a")], ["a"], "2"));
        Assert.Throws<InvalidDataException>(() => PluginDependencyGraph.Resolve([Plugin("a", new PluginDependency("b", ">1.0.0")), Plugin("b")], ["a"], "1"));
    }

}
