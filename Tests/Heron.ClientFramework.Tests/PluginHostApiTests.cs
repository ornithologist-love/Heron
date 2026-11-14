using System.Text.Json;
using Heron.ClientControls.Buttons;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.Runtime;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md#host-interface-identity")]
public sealed class PluginHostApiTests
{
    private static PluginManifest Manifest(string api) => new()
    {
        Id = "fixture", Version = "1.0.0", HostApi = api,
        Modules = [new("main", "main.dll", "Fixture")]
    };

    [Theory]
    [InlineData("7", "7.0")]
    [InlineData("7.0", "7")]
    [InlineData("7", "7.2")]
    [InlineData("7.2", "7.10")]
    [InlineData("7.2", "7.2")]
    public void HostAcceptsTheSameMajorAndAtLeastTheRequiredMinor(string required, string host)
    {
        Assert.Single(PluginDependencyGraph.Resolve([Manifest(required)], ["fixture"], host).LoadOrder);
    }

    [Theory]
    [InlineData("7.10", "7.2")]
    [InlineData("7.1", "7")]
    [InlineData("8.0", "7.2")]
    [InlineData("7.0", "8.0")]
    public void HostRejectsUnsupportedRequirementsBeforeActivation(string required, string host)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            PluginDependencyGraph.Resolve([Manifest(required)], ["fixture"], host));
        Assert.Contains(PluginHostApiVersion.Parse(required).ToString(), error.Message);
        Assert.Contains(PluginHostApiVersion.Parse(host).ToString(), error.Message);
    }

    [Theory]
    [InlineData("7.0.0")]
    [InlineData("7.")]
    [InlineData("-1")]
    [InlineData("unknown")]
    [InlineData("7.2147483648")]
    public void InvalidHostApiVersionsAreRejected(string value)
    {
        Assert.Throws<InvalidDataException>(() => PluginDependencyGraph.Resolve([Manifest(value)], ["fixture"], "7.2"));
        Assert.Throws<InvalidDataException>(() => PluginDependencyGraph.Resolve([], [], value));
    }

    [Fact]
    public void IntegerLockRemainsReadableAndMinorUpgradeRefreshesAssemblyCache()
    {
        var package = PluginPackage.Read(Path.Combine(AppContext.BaseDirectory, "plugins/Managed1/plugin.json"));
        var edition = new PluginEdition("fixture", [package.Manifest.Id]);
        var root = Path.Combine(Path.GetTempPath(), "heron-host-api-" + Guid.NewGuid().ToString("N"));
        try
        {
            PluginEditionAssemblyCache.Assemble(edition, [package], root, hostApi: "1");
            var stamp = File.ReadAllText(Path.Combine(root, ".plugin-assembly.fingerprint"));
            var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            var previous = new PluginEditionLock(edition.Name, "1", [new(package.Manifest.Id, package.Manifest.Version)]);
            var restored = JsonSerializer.Deserialize<PluginEditionLock>(JsonSerializer.Serialize(previous, json), json)!;
            Assert.True(PluginHostApiVersion.Parse("1.2").Supports(PluginHostApiVersion.Parse(restored.HostApi)));

            var current = PluginEditionAssemblyCache.Assemble(edition, [package], root, hostApi: "1.2");
            var persisted = JsonSerializer.Deserialize<PluginEditionLock>(File.ReadAllText(Path.Combine(root, "plugins.lock.json")), json)!;
            Assert.Equal("1.2", current.HostApi);
            Assert.Equal(current.HostApi, persisted.HostApi);
            Assert.NotEqual(stamp, File.ReadAllText(Path.Combine(root, ".plugin-assembly.fingerprint")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PluginCompiledAgainstPreviousContractsActivatesWithCurrentSharedAssemblies()
    {
        var package = PluginPackage.Read(Path.Combine(AppContext.BaseDirectory, "plugins/BaselineContract/plugin.json"));
        Assert.Single(PluginDependencyGraph.Resolve([package.Manifest], [package.Manifest.Id], "7.2").LoadOrder);
        var registry = new ClientPluginRegistry();
        await using var instance = new ClientPluginInstance(package,
            [typeof(IconButton).Assembly, typeof(Avalonia.Controls.Control).Assembly,
             typeof(Avalonia.AvaloniaObject).Assembly, typeof(FluentIcons.Common.Icon).Assembly,
             typeof(FluentIcons.Avalonia.FluentIcon).Assembly], registry);
        await instance.StartAsync(CancellationToken.None);
        var command = Assert.Single(registry.Commands);
        Assert.Equal(typeof(IconButton).Assembly.GetName().Name, command.Title);
        await command.Execute(CancellationToken.None);
        await instance.StopAsync(CancellationToken.None);
        Assert.Empty(registry.Commands);
    }
}
