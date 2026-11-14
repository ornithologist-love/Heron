using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.Runtime;
using Xunit;

namespace Heron.ClientFramework.Tests;

/// <summary>Startup selection rules of the product-neutral plugin host.</summary>
[Anchor("Docs/client-framework.md")]
public sealed class PluginHostStartupTests : IDisposable
{
    private const string HostApi = "1";
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heron-plugin-host-tests", Guid.NewGuid().ToString("N"));
    private readonly PluginVersionStore _store;

    public PluginHostStartupTests()
    {
        Directory.CreateDirectory(_root);
        _store = new PluginVersionStore(Path.Combine(_root, "store"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static PluginManifest Manifest(string id, string version) =>
        new() { Id = id, Version = version, HostApi = HostApi, Modules = [new("Main", "main.dll", "Main")] };

    private PluginPackage Bundled(string id, string version)
    {
        var directory = Path.Combine(_root, "builtin", id, version);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "plugin.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Manifest(id, version), ManifestJson));
        File.WriteAllText(Path.Combine(directory, "main.dll"), "fixture");
        return PluginPackage.Read(path);
    }

    private async Task<PluginPackage> InstallAsync(string id, string version)
    {
        var archive = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                writer.Write(JsonSerializer.Serialize(Manifest(id, version), ManifestJson));
            using var dll = new StreamWriter(zip.CreateEntry("main.dll").Open());
            dll.Write("fixture");
        }
        return await _store.InstallArchiveAsync(archive, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))));
    }

    private void Activate(params PluginPackage[] packages)
    {
        _store.Stage(new PluginEditionLock("full", HostApi,
            packages.Select(p => new PluginLockEntry(p.Manifest.Id, p.Manifest.Version)).ToArray()), packages);
        _store.BeginStartup();
        _store.MarkHealthy();
    }

    private static string[] Selected(ClientPluginStartupSelection selection) =>
        selection.Packages.Select(p => $"{p.Manifest.Id}@{p.Manifest.Version}").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Without_a_version_group_the_bundled_packages_start()
    {
        var bundled = Bundled("sample", "1.0.0");
        var selection = ClientPluginHostCore.SelectStartup([bundled], new PluginEdition("full", ["sample"]),
            _store, HostApi, bundledOnlyPlatform: null, followShippedEdition: false);
        Assert.Equal(new[] { "sample@1.0.0" }, Selected(selection));
        Assert.Null(selection.RecoveryMessage);
    }

    [Fact]
    public async Task A_healthy_installed_group_overrides_the_bundled_packages()
    {
        var bundled = Bundled("sample", "1.0.0");
        Activate(await InstallAsync("sample", "1.1.0"));
        var selection = ClientPluginHostCore.SelectStartup([bundled], new PluginEdition("full", ["sample"]),
            _store, HostApi, bundledOnlyPlatform: null, followShippedEdition: false);
        Assert.Equal(new[] { "sample@1.1.0" }, Selected(selection));
    }

    [Fact]
    public async Task An_unresolvable_override_falls_back_to_bundled_packages_and_resets_the_store()
    {
        var bundled = Bundled("sample", "1.0.0");
        Activate(await InstallAsync("sample", "1.1.0"));
        Directory.Delete(Path.Combine(_root, "store", "versions"), true); // the pinned version is gone

        var selection = ClientPluginHostCore.SelectStartup([bundled], new PluginEdition("full", ["sample"]),
            _store, HostApi, bundledOnlyPlatform: null, followShippedEdition: false,
            new ClientPluginHostText { RecoveredPrefix = "recovered: " });

        Assert.Equal(new[] { "sample@1.0.0" }, Selected(selection));
        Assert.StartsWith("recovered: ", selection.RecoveryMessage);
        Assert.False(_store.ReadState().HasActive);
    }

    [Fact]
    public async Task Following_the_shipped_edition_drops_foreign_plugins_and_raises_older_pins()
    {
        var bundled = Bundled("sample", "1.2.0");
        Activate(await InstallAsync("sample", "1.0.0"), await InstallAsync("extra", "1.0.0"));
        var edition = new PluginEdition("agent", ["sample"]);

        var followed = ClientPluginHostCore.SelectStartup([bundled], edition, _store, HostApi,
            bundledOnlyPlatform: null, followShippedEdition: true);
        Assert.Equal(new[] { "sample@1.2.0" }, Selected(followed));
    }

    [Fact]
    public async Task Without_following_the_edition_the_installed_group_is_kept()
    {
        var bundled = Bundled("sample", "1.2.0");
        Activate(await InstallAsync("sample", "1.0.0"), await InstallAsync("extra", "1.0.0"));

        var selection = ClientPluginHostCore.SelectStartup([bundled], new PluginEdition("full", ["sample"]),
            _store, HostApi, bundledOnlyPlatform: null, followShippedEdition: false);
        Assert.Equal(new[] { "extra@1.0.0", "sample@1.0.0" }, Selected(selection));
    }

    [Fact]
    public async Task A_bundled_only_platform_ignores_installed_overrides()
    {
        var bundled = Bundled("sample", "1.0.0");
        Activate(await InstallAsync("sample", "1.1.0"));
        var edition = new PluginEdition("full", ["sample"]) { PlatformAllowList = ["Win64", "Mac"] };

        var selection = ClientPluginHostCore.SelectStartup([bundled], edition, _store, HostApi,
            bundledOnlyPlatform: "Mac", followShippedEdition: false);
        Assert.Equal(new[] { "sample@1.0.0" }, Selected(selection));
        Assert.True(_store.ReadState().HasActive, "a bundled-only platform never rewrites the installed group");
    }
}
