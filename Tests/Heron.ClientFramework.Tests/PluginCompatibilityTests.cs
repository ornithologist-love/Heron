using System.Reflection;
using System.Runtime.Loader;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.PluginProbe.Contracts;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md")]
public sealed class PluginCompatibilityTests : IDisposable
{
    private readonly AssemblyLoadContext _hostContext = new(null, isCollectible: true);

    public void Dispose() => _hostContext.Unload();

    [Fact]
    public async Task ReviewedForwardIdentityPairLoadsTheHostDependency()
    {
        var (package, requested) = Fixture();
        var host = HostDependency("CompatiblePrivate");
        var pairs = new Dictionary<string, string> { [requested.FullName] = host.FullName! };
        using var session = new PluginAssemblySession(package, [typeof(IProbe).Assembly, host],
            compatibleAssemblyVersions: pairs);
        await using var probe = session.Create<IProbe>("main");

        Assert.Equal("private-2", probe.DependencyIdentity);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("requested-version")]
    [InlineData("requested-name")]
    [InlineData("host-version")]
    [InlineData("host-culture")]
    [InlineData("host-token")]
    public async Task UnreviewedIdentityIsRejectedAtTheLoadingBoundary(string mismatch)
    {
        var (package, requested) = Fixture();
        var host = HostDependency("CompatiblePrivate");
        var declaredRequest = new AssemblyName(requested.FullName);
        var declaredHost = new AssemblyName(host.FullName!);
        switch (mismatch)
        {
            case "requested-version": declaredRequest.Version = NextMinor(requested.Version!); break;
            case "requested-name": declaredRequest.Name += ".Other"; break;
            case "host-version": declaredHost.Version = NextMinor(declaredHost.Version!); break;
            case "host-culture": declaredHost.CultureName = "en-US"; break;
            case "host-token": declaredHost.SetPublicKeyToken(new byte[8]); break;
        }
        var pairs = new Dictionary<string, string>();
        if (mismatch != "missing") pairs.Add(declaredRequest.FullName, declaredHost.FullName);
        using var session = new PluginAssemblySession(package, [typeof(IProbe).Assembly, host],
            compatibleAssemblyVersions: pairs);
        await using var probe = session.Create<IProbe>("main");

        Assert.Throws<FileLoadException>(() => _ = probe.DependencyIdentity);
    }

    [Fact]
    public async Task DeclaredPairCannotCrossAMajorVersion()
    {
        var (package, requested) = Fixture();
        var host = HostDependency("Managed2");
        var pairs = new Dictionary<string, string> { [requested.FullName] = host.FullName! };
        using var session = new PluginAssemblySession(package, [typeof(IProbe).Assembly, host],
            compatibleAssemblyVersions: pairs);
        await using var probe = session.Create<IProbe>("main");

        Assert.Throws<FileLoadException>(() => _ = probe.DependencyIdentity);
    }

    private static (PluginPackage Package, AssemblyName Dependency) Fixture()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugins", "Managed1");
        return (PluginPackage.Read(Path.Combine(directory, "plugin.json")),
            AssemblyName.GetAssemblyName(Path.Combine(directory, "Heron.ClientFramework.PluginProbe.Private.dll")));
    }

    private static Version NextMinor(Version version) => new(version.Major, version.Minor + 1, 0, 0);

    private Assembly HostDependency(string fixture) => _hostContext.LoadFromAssemblyPath(
        Path.Combine(AppContext.BaseDirectory, "plugins", fixture, "Heron.ClientFramework.PluginProbe.Private.dll"));
}
