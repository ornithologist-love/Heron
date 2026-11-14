using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.PluginProbe.Contracts;
using FluentAssertions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md")]
[Anchor("Docs/client-framework.md")]
public sealed class PluginLoadingTests
{
    private static PluginPackage Package(string name) => PluginPackage.Read(
        Path.Combine(AppContext.BaseDirectory, "plugins", name, "plugin.json"));

    [Fact]
    public void IncompatibleSharedContractIsRejectedInsteadOfLoadingAPrivateCopy()
    {
        var incompatible = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Heron.ClientFramework.PluginProbe.Contracts") { Version = new Version(9, 0, 0, 0) },
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
        using var session = new PluginAssemblySession(Package("Managed1"), [incompatible]);
        Action create = () => session.Create<IProbe>("main");
        create.Should().Throw<Exception>().Which.ToString().Should().Contain("Shared assembly mismatch");
    }

    [Fact]
    public void SameNamedPrivateDependenciesAreIsolated_AndBothContextsCanBeCollected()
    {
        var references = LoadBoth();
        foreach (var reference in references) AssertCollected(reference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] LoadBoth()
    {
        using var first = new PluginAssemblySession(Package("Managed1"), [typeof(IProbe).Assembly]);
        using var second = new PluginAssemblySession(Package("Managed2"), [typeof(IProbe).Assembly]);
        var one = first.Create<IProbe>("main");
        var two = second.Create<IProbe>("main");
        one.DependencyIdentity.Should().Be("private-1");
        two.DependencyIdentity.Should().Be("private-2");
        one.GetType().Assembly.GetName().Name.Should().Be(two.GetType().Assembly.GetName().Name);
        AssemblyLoadContext.GetLoadContext(one.GetType().Assembly).Should().NotBe(AssemblyLoadContext.GetLoadContext(two.GetType().Assembly));
        one.DisposeAsync().AsTask().GetAwaiter().GetResult();
        two.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return [first.UnloadReference, second.UnloadReference];
    }

    [Fact]
    public void StopWaitsForWorkerAndRemovesHostEventSubscription()
    {
        AssertCollected(StartAndStop());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartAndStop()
    {
        using var session = new PluginAssemblySession(Package("Managed1"), [typeof(IProbe).Assembly]);
        var probe = session.Create<IProbe>("main");
        var ticks = 0;
        probe.StartAsync(() => Interlocked.Increment(ref ticks)).GetAwaiter().GetResult();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 2, TimeSpan.FromSeconds(3)).Should().BeTrue();
        var beforeEvent = ticks;
        ProbeEvents.Raise();
        ticks.Should().BeGreaterThan(beforeEvent);
        probe.StopAsync().GetAwaiter().GetResult();
        var stopped = ticks;
        ProbeEvents.Raise();
        Thread.Sleep(60);
        ticks.Should().Be(stopped);
        probe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return session.UnloadReference;
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("../../outside.dll")]
    public void ManifestCannotSelectAnAssemblyOutsideThePackage(string relativePath)
    {
        using var folder = new TemporaryFolder();
        var manifest = Path.Combine(folder.Path, "plugin.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            id = "probe", version = "1.0.0", host_api = "1",
            modules = new[] { new { name = "main", assembly = relativePath, entry_type = "Probe" } }
        }));
        Action load = () => PluginPackage.Read(manifest);
        load.Should().Throw<InvalidDataException>();
    }

    private static void AssertCollected(WeakReference reference)
    {
        for (var i = 0; reference.IsAlive && i < 20; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(20);
        }
        reference.IsAlive.Should().BeFalse("all plugin-owned objects, workers and subscriptions were released");
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "heron-plugin-tests", Guid.NewGuid().ToString("N"));
        public TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
