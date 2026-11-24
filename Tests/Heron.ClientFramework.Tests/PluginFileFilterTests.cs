using Heron.ClientFramework.Plugins;
using Xunit;

namespace Heron.ClientFramework.Tests;

/// <summary>
///     The consumer's file filter decides what an assembly copies and what its cache validates; its fingerprint
///     takes part in reuse, so changing the rules cannot reuse an output assembled under the old ones.
/// </summary>
[Anchor("Docs/client-framework.md#packaging-policy-and-atomic-delivery")]
public sealed class PluginFileFilterTests
{
    private sealed class ExcludeSymbols(string fingerprint) : IPluginFileFilter
    {
        public string Fingerprint { get; } = fingerprint;
        public bool Include(string relativePath) => !relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filtered_files_are_not_copied_and_a_changed_fingerprint_reassembles()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-file-filter-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "package");
        var output = Path.Combine(root, "output");
        var stampPath = Path.Combine(output, ".plugin-assembly.fingerprint");
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "plugins/Managed1");
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(source, Path.GetRelativePath(fixture, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            // Filtering depends on a package path, not the compiler's symbol format.
            File.WriteAllText(Path.Combine(source, "excluded.pdb"), "isolated filter fixture");
            var package = PluginPackage.Read(Path.Combine(source, "plugin.json"));
            var edition = new PluginEdition("fixture", [package.Manifest.Id]);
            PluginEditionAssemblyCache.Assemble(edition, [package], output, new ExcludeSymbols("rules-a"));
            Assert.Empty(Directory.EnumerateFiles(output, "*.pdb", SearchOption.AllDirectories));
            var first = File.ReadAllText(stampPath);

            PluginEditionAssemblyCache.Assemble(edition, [package], output, new ExcludeSymbols("rules-a"));
            Assert.Equal(first, File.ReadAllText(stampPath));

            PluginEditionAssemblyCache.Assemble(edition, [package], output, new ExcludeSymbols("rules-b"));
            Assert.NotEqual(first, File.ReadAllText(stampPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Platform_runtime_filter_retains_only_target_platform_native_payloads()
    {
        var win64 = PlatformRuntimeFileFilter.ForPlatform("Win64");
        Assert.True(win64.Include("runtimes/win-x64/native/p4bridge.dll"));
        Assert.True(win64.Include("runtimes/win/native/foo.dll"));
        Assert.True(win64.Include("runtimes/any/native/foo.dll"));
        Assert.True(win64.Include("plugin.json"));
        Assert.False(win64.Include("runtimes\\win-x86\\native\\p4bridge.dll"));
        Assert.False(win64.Include("runtimes/linux-x64/native/libp4bridge.so"));
        Assert.False(win64.Include("runtimes/linux-arm64/native/libp4bridge.so"));
        Assert.False(win64.Include("runtimes/osx-u/native/libp4bridge.dylib"));

        var mac = PlatformRuntimeFileFilter.ForPlatform("Mac");
        Assert.True(mac.Include("runtimes/osx-u/native/libp4bridge.dylib"));
        Assert.True(mac.Include("runtimes/osx-arm64/native/foo.dylib"));
        Assert.False(mac.Include("runtimes/win-x64/native/p4bridge.dll"));

        var linux = PlatformRuntimeFileFilter.ForPlatform("Linux");
        Assert.True(linux.Include("runtimes/linux-x64/native/libp4bridge.so"));
        Assert.False(linux.Include("runtimes/win-x64/native/p4bridge.dll"));

        Assert.NotEqual(win64.Fingerprint, mac.Fingerprint);
        Assert.NotEqual(mac.Fingerprint, linux.Fingerprint);
    }
}
