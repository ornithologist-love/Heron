using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Heron.ClientFramework.Plugins;
using Xunit;

namespace Heron.ClientFramework.Tests;

[Anchor("Docs/client-framework.md")]
public sealed class PluginVersionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "heron-plugin-store-tests", Guid.NewGuid().ToString("N"));
    private readonly PluginVersionStore _store;
    public PluginVersionStoreTests() { Directory.CreateDirectory(_root); _store = new(Path.Combine(_root, "store")); }
    private (string Path, string Hash) Archive(string version = "1.0.0", string? maliciousPath = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                writer.Write(JsonSerializer.Serialize(new PluginManifest { Id = "sample", Version = version, HostApi = "1", Modules = [new("Main", "main.dll", "Main")] },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            using var dll = new StreamWriter(zip.CreateEntry(maliciousPath ?? "main.dll").Open());
            dll.Write("fixture");
        }
        return (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Fact]
    public async Task Corruption_does_not_install_a_version()
    {
        var archive = Archive();
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.InstallArchiveAsync(archive.Path, new string('0', 64)));
        Assert.Empty(_store.InstalledPackages());
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("C:/escape.dll")]
    public async Task Archive_paths_cannot_escape(string path)
    {
        var archive = Archive(maliciousPath: path);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.InstallArchiveAsync(archive.Path, archive.Hash));
        Assert.Empty(_store.InstalledPackages());
    }

    [Fact]
    public async Task Interrupted_trial_recovers_last_healthy_group()
    {
        var a = Archive();
        var first = await _store.InstallArchiveAsync(a.Path, a.Hash);
        _store.Stage(new("full", "1", [new("sample", "1.0.0")]), [first]);
        Assert.Equal("1.0.0", Assert.Single(_store.BeginStartup()).Version);
        _store.MarkHealthy();
        var b = Archive("1.1.0");
        var second = await _store.InstallArchiveAsync(b.Path, b.Hash);
        _store.Stage(new("full", "1", [new("sample", "1.1.0")]), [first, second]);
        Assert.Equal("1.1.0", Assert.Single(_store.BeginStartup()).Version);
        var restarted = new PluginVersionStore(Path.Combine(_root, "store"));
        Assert.Equal("1.0.0", Assert.Single(restarted.BeginStartup()).Version);
        Assert.Null(restarted.ReadState().Pending);
    }

    [Fact]
    public async Task First_trial_failure_and_explicit_reset_restore_builtin_selection()
    {
        var archive = Archive();
        var installed = await _store.InstallArchiveAsync(archive.Path, archive.Hash);
        _store.Stage(new("full", "1", [new("sample", "1.0.0")]), [installed]);
        _store.BeginStartup();
        Assert.Empty(_store.BeginStartup());
        Assert.False(_store.ReadState().HasActive);
        _store.Stage(new("full", "1", [new("sample", "1.0.0")]), [installed]);
        _store.BeginStartup();
        _store.MarkHealthy();
        _store.ResetToBuiltin();
        Assert.False(_store.ReadState().HasActive);
        Assert.Single(_store.InstalledPackages());
    }

    public void Dispose() => Directory.Delete(_root, true);
}
