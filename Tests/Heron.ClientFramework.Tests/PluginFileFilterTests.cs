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
}
