using FluentAssertions;
using Heron.Pregel.Automation;
using Heron.Pregel.Tooling;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Heron.AutomationTenants.Tests;

[Collection("Tenant runtime")]
[Anchor("Docs/automation-tenants.md")]
public sealed class PublishedSourceStoreTests
{
    [Fact]
    public void Files_copied_out_of_an_installation_or_snapshot_are_writable_while_the_package_rejects_edits()
    {
        using var scope = new InstalledScope();
        var entry = scope.Install("Mock", ("Mock/Mock.csx", "// mock"), ("Mock/assets/Case.json", "{\"account\":\"\"}"));
        var (snapshot, _) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);
        var snapshotCase = Path.Combine(Path.GetDirectoryName(snapshot)!, "assets", "Case.json");

        foreach (var source in new[] { Path.Combine(scope.Scripts, "Mock", "assets", "Case.json"), snapshotCase })
        {
            var copy = Path.Combine(scope.Output, Guid.NewGuid().ToString("N") + ".json");
            File.Copy(source, copy);
            File.WriteAllText(copy, "{\"account\":\"mock\"}");
            File.ReadAllText(copy).Should().Contain("mock", "a template copied out of the package is the run's own file");
        }
        if (OperatingSystem.IsWindows())
        {
            FluentActions.Invoking(() => File.WriteAllText(Path.Combine(scope.Scripts, "Mock", "assets", "Case.json"), "{}"))
                .Should().Throw<UnauthorizedAccessException>("installed code is publisher-owned");
            FluentActions.Invoking(() => File.WriteAllText(snapshotCase, "{}"))
                .Should().Throw<UnauthorizedAccessException>("an admitted snapshot is immutable");
        }
    }

    [Fact]
    public void Protection_from_earlier_releases_is_migrated_off_individual_files()
    {
        using var scope = new InstalledScope();
        scope.Install("Mock", ("Mock/Mock.csx", "// mock"));
        var file = Path.Combine(scope.Scripts, "Mock", "Mock.csx");
        AutomationPublishedSource.MakeWritable(scope.Scripts);
        if (OperatingSystem.IsWindows()) File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        else File.SetUnixFileMode(file, UnixFileMode.UserRead);
        AutomationPublishedSource.Protect(scope.Scripts);

        AutomationPublishedSource.IsReadOnlyTree(Path.Combine(scope.Scripts, "Mock")).Should().BeTrue();
        if (OperatingSystem.IsWindows()) (File.GetAttributes(file) & FileAttributes.ReadOnly).Should().Be((FileAttributes)0);
        else (File.GetUnixFileMode(file) & UnixFileMode.UserWrite).Should().Be(UnixFileMode.UserWrite);
    }

    [Fact]
    public void A_new_version_stores_only_the_files_that_changed()
    {
        using var scope = new InstalledScope();
        var large = new string('x', 256 * 1024);
        var entry = scope.Install("Mock", ("Mock/Mock.csx", "// first"), ("Mock/tools/payload.bin", large));
        AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);
        var before = Directory.GetFiles(AutomationContentStore.BlobRoot);

        scope.Install("Mock", ("Mock/Mock.csx", "// second"), ("Mock/tools/payload.bin", large));
        var (second, version) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);

        var added = Directory.GetFiles(AutomationContentStore.BlobRoot).Except(before).ToArray();
        added.Should().ContainSingle("the unchanged payload and Shared files are shared by both versions");
        File.ReadAllText(second).Should().Be("// second");
        version!.SnapshotRoot.Should().StartWith(AutomationContentStore.VersionRoot);
    }

    [Fact]
    public void A_damaged_snapshot_is_rebuilt_from_the_verified_installation()
    {
        using var scope = new InstalledScope();
        var entry = scope.Install("Mock", ("Mock/Mock.csx", "// mock"));
        var (snapshot, _) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(snapshot)!, "injected.csx"), "// not published");

        var (rebuilt, _) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);

        rebuilt.Should().Be(snapshot);
        File.Exists(Path.Combine(Path.GetDirectoryName(rebuilt)!, "injected.csx")).Should().BeFalse();
    }

    [Fact]
    public void Retention_keeps_recent_and_latest_versions_and_removes_unused_blobs()
    {
        using var scope = new InstalledScope();
        var entry = scope.Install("Mock", ("Mock/Mock.csx", "// v0"));
        var bundles = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            scope.Install("Mock", ("Mock/Mock.csx", "// v" + i));
            var (path, _) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);
            bundles.Add(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!));
        }
        var old = DateTimeOffset.UtcNow - AutomationContentStore.RetentionWindow - TimeSpan.FromDays(1);
        for (var i = 0; i < bundles.Count; i++) scope.SetLastUsed(bundles[i], old + TimeSpan.FromMinutes(i));
        var legacy = Path.Combine(AutomationStore.DefaultRoot, "source-snapshots", "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "old.csx"), "// old");
        if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(legacy, "old.csx"), FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(legacy, old.UtcDateTime);

        AutomationContentStore.Collect(DateTimeOffset.UtcNow);

        Directory.Exists(Path.Combine(AutomationContentStore.VersionRoot, bundles[0])).Should().BeFalse();
        Directory.Exists(Path.Combine(AutomationContentStore.VersionRoot, bundles[1])).Should().BeFalse();
        Directory.Exists(Path.Combine(AutomationContentStore.VersionRoot, bundles[2])).Should().BeTrue("the two latest versions remain for rollback");
        Directory.Exists(Path.Combine(AutomationContentStore.VersionRoot, bundles[3])).Should().BeTrue();
        Directory.GetFiles(AutomationContentStore.BlobRoot).Select(Path.GetFileName)
            .Should().BeEquivalentTo(scope.ManifestBlobs(bundles[2]).Union(scope.ManifestBlobs(bundles[3])));
        Directory.Exists(legacy).Should().BeFalse("full-copy snapshots from earlier releases age out as well");
    }

    [Fact]
    public void Writes_into_a_package_get_guidance_and_scripts_get_a_writable_cache()
    {
        using var scope = new InstalledScope();
        var entry = scope.Install("Mock", ("Mock/Mock.csx", "// mock"));
        var (snapshot, _) = AutomationLocalSource.Freeze(entry, scope.State, CancellationToken.None);
        var directory = Path.GetDirectoryName(snapshot)!;

        AutomationContentStore.ErrorHint($"Access to the path '{Path.Combine(directory, "out.bin")}' is denied.")
            .Should().Contain("WorkingDirectory");
        AutomationContentStore.ErrorHint("Access to the path 'D:\\Game\\Saved\\Case.json' is denied.").Should().BeNull();
        AutomationContentStore.CacheDirectory(directory).Should().Be(Path.Combine(AutomationContentStore.CacheRoot, "Mock"));
        AutomationContentStore.CacheDirectory(Path.Combine(scope.Scripts, "Mock")).Should().Be(Path.Combine(AutomationContentStore.CacheRoot, "Mock"));
        AutomationContentStore.CacheDirectory(scope.Output).Should().BeNull("a local working copy keeps its own cache");
    }

    [Fact]
    public async Task Delivery_replaces_an_installation_an_agent_edited_instead_of_being_blocked_by_it()
    {
        await using var publisher = new MockPublisher();
        using var scope = new InstalledScope(publisher.Address + "source/");
        var entry = Path.Combine(scope.Scripts, "Mock", "Mock.csx");
        publisher.Publish(("Mock/Mock.csx", "// published v1"), ("Mock/assets/Case.json", "{}"));
        var source = new AutomationRemoteSource();
        await source.EnsurePathAsync(entry, change => change(), CancellationToken.None);
        File.ReadAllText(entry).Should().Be("// published v1");

        // An agent bypasses the guardrail, edits the script and adds a file.
        AutomationPublishedSource.MakeWritable(scope.Scripts);
        File.WriteAllText(entry, "// edited by an agent");
        File.WriteAllText(Path.Combine(scope.Scripts, "Mock", "extra.csx"), "// unpublished");
        source.NeedsUpdate(source.Manifest!.Scripts[0]).Should().BeTrue("verification detects the private edit");
        await source.EnsurePathAsync(entry, change => change(), CancellationToken.None);
        File.ReadAllText(entry).Should().Be("// published v1");
        File.Exists(Path.Combine(scope.Scripts, "Mock", "extra.csx")).Should().BeFalse();

        publisher.Publish(("Mock/Mock.csx", "// published v2"), ("Mock/assets/Case.json", "{}"));
        await source.EnsurePathAsync(entry, change => change(), CancellationToken.None);
        File.ReadAllText(entry).Should().Be("// published v2", "the next delivery is not blocked by the earlier edit");
        var copy = Path.Combine(scope.Output, "Case.json");
        File.Copy(Path.Combine(scope.Scripts, "Mock", "assets", "Case.json"), copy);
        File.WriteAllText(copy, "{\"quality\":\"high\"}");
    }

    [Fact]
    public async Task Deployment_only_source_installs_the_requested_version_and_reloads_it_locally()
    {
        await using var publisher = new MockPublisher();
        using var scope = new InstalledScope(publisher.Address + "source/");
        publisher.Publish(("Mock/Mock.csx", """
            return Pregel.Define<string>("mock", "1").Start(Echo).Compile();
            static string Echo(string input, PregelNodeContext context, CancellationToken token) => input;
            """));
        var entry = (await new AutomationOfficialScripts().ListAsync()).Single();
        var request = new RemoteAutomationDeployment
        {
            TaskId = RemoteAutomationOfficialScripts.TaskId(entry),
            Source = new() { SourceId = entry.Id, Revision = entry.Revision, Sha256 = entry.ArchiveSha256,
                SharedRevision = entry.SharedRevision, SharedSha256 = entry.SharedArchiveSha256 }
        };
        var previous = AutomationRuntime.ScriptSource;
        AutomationRuntime.ScriptSource = new DeploymentOnlySource();
        try
        {
            await using var host = new AutomationHost(new AutomationStore(scope.State), scope.Output,
                true, null, null, null, schedulingEnabled: false);
            var worker = new RemoteAutomationOfficialScripts(host, RemoteAutomationEnvironment.Current);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.Scripts.DownloadAndLoadAsync(entry.Path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.PrepareDeploymentAsync(
                request with { Source = request.Source with { Sha256 = new string('0', 64) } }, CancellationToken.None));
            File.Exists(entry.Path).Should().BeFalse();

            var draft = await worker.PrepareDeploymentAsync(request, CancellationToken.None);
            (await host.Scripts.LoadAsync(entry.Path)).Should().Be(draft.Template.Id);
        }
        finally { AutomationRuntime.ScriptSource = previous; }
    }

    private sealed class DeploymentOnlySource : IAutomationScriptSource
    {
        private readonly AutomationRemoteSource _source = new();
        public string DirectoryLabel => "Mock source";
        public string Url => _source.Url;
        public string Root => _source.Root;
        public string? Error => null;
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        public string InstalledStamp(string path) => _source.InstalledStamp(path);
        public IDisposable? AcquireReadLease(string path) => AutomationRemoteSource.AcquireReadLease(path);
        public IReadOnlyList<AutomationScriptEntry> ListEntries(IReadOnlyList<AutomationScriptEntry> local,
            Func<string, string?> loadedStamp) => [];
        public Task InstallAsync(string path, Action<bool, Action> commit, CancellationToken token) =>
            throw new InvalidOperationException("Interactive downloads are disabled");
        public Task PrepareForLoadAsync(string path, Action<bool, Action> commit, CancellationToken token) => Task.CompletedTask;
        public Task InstallForDeploymentAsync(string path, Action<bool, Action> commit, CancellationToken token) =>
            _source.InstallAsync(path, commit, token);
    }

    /// <summary>A loopback official source that serves a catalog and archives built from in-memory files.</summary>
    private sealed class MockPublisher : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private byte[] _catalog = [];

        public string Address { get; } = LocalEndpoint.Address();

        public MockPublisher()
        {
            _listener.Prefixes.Add(Address);
            _listener.Start();
            _loop = Listen();
        }

        public void Publish(params (string Path, string Content)[] files)
        {
            var shared = Package("Shared", "", [("Shared/Common.csx", "// shared")]);
            var script = Package("Mock", files[0].Path, files);
            _catalog = JsonSerializer.SerializeToUtf8Bytes(new AutomationRemoteManifest(shared, [script]), AutomationRemoteSource.Json);
        }

        private AutomationRemotePackage Package(string id, string entry, (string Path, string Content)[] files)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var (path, content) in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                    writer.Write(content);
                }
            var bytes = buffer.ToArray();
            using var read = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var revision = AutomationScriptRevision.Compute(read, id, entry);
            var archive = id + "-" + revision["sha256-".Length..][..12] + ".zip";
            _files[archive] = bytes;
            return new(id, revision, archive, Convert.ToHexString(SHA256.HashData(bytes)), entry, id);
        }

        private async Task Listen()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    var path = context.Request.Url!.AbsolutePath;
                    var body = path == "/source/catalog.json" ? _catalog
                        : path.StartsWith("/source/files/", StringComparison.Ordinal) && _files.TryGetValue(path["/source/files/".Length..], out var file)
                            ? file : null;
                    context.Response.StatusCode = body == null ? 404 : 200;
                    if (body != null) await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Close();
            try { await _loop; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>An isolated script root with published-looking installs of a mock suite and Shared.</summary>
    private sealed class InstalledScope : IDisposable
    {
        private readonly TempDirectory _temporary = new();
        private readonly RemoteAutomationEnvironment? _previous = RemoteAutomationEnvironment.CurrentOrNull;
        private readonly Func<string>? _previousData = AutomationRuntime.DataRootProvider;

        public string Scripts => Path.Combine(_temporary.Path, ".mock", "Pregel");
        public string State => Path.Combine(_temporary.Path, "automation");
        public string Output => Path.Combine(_temporary.Path, "output");

        public InstalledScope(string source = "http://127.0.0.1:9/source/")
        {
            Directory.CreateDirectory(Output);
            AutomationRuntime.DataRootProvider = () => Path.Combine(_temporary.Path, "data");
            RemoteAutomationEnvironment.Configure(new()
            {
                LocalRoot = () => _temporary.Path, ScriptsRoot = () => Scripts, SharedRoot = () => Path.Combine(Scripts, "Shared"),
                ClientInstance = () => "mock", AgentConfig = () => new(false, null), DefaultServerUrl = () => null,
                ResourceServerUrl = () => null, ResourceDomainDirectory = () => Path.Combine(_temporary.Path, "resource-domain"),
                ResourceQueueTimeout = () => null, OfficialSourceUrl = source,
                StartProcess = (_, _, _) => throw new InvalidOperationException("No process starts in store tests"),
                QueryPublishedSource = (_, _, _) => Task.FromResult(new RemoteAutomationPublishedSource("", null))
            });
        }

        /// <summary>Replaces the suite's installed files with a baseline, as a verified install leaves them.</summary>
        public string Install(string id, params (string Path, string Content)[] files)
        {
            AutomationPublishedSource.MakeWritable(Scripts);
            foreach (var suite in new[] { id, "Shared" })
            {
                var directory = Path.Combine(Scripts, suite);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(Path.Combine(Scripts, "Shared", "Common.csx"), "// shared");
            foreach (var (path, content) in files)
            {
                var file = Path.Combine(Scripts, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content);
            }
            var entry = files[0].Path;
            foreach (var (suite, suiteEntry) in new[] { (id, entry), ("Shared", "") })
            {
                var directory = Path.Combine(Scripts, suite);
                var hash = AutomationLocalSource.ContentHash(directory, suite, suiteEntry);
                AutomationLocalSource.WriteBaseline(directory, suite, suiteEntry, hash, new string('0', 64), hash);
            }
            AutomationPublishedSource.Protect(Scripts);
            return Path.Combine(Scripts, entry.Replace('/', Path.DirectorySeparatorChar));
        }

        public void SetLastUsed(string bundle, DateTimeOffset when)
        {
            var path = Path.Combine(AutomationContentStore.VersionRoot, bundle + ".json");
            var manifest = System.Text.Json.JsonSerializer.Deserialize<AutomationContentStore.VersionManifest>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest with { LastUsed = when },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        }

        public string[] ManifestBlobs(string bundle) => System.Text.Json.JsonSerializer.Deserialize<AutomationContentStore.VersionManifest>(
            File.ReadAllText(Path.Combine(AutomationContentStore.VersionRoot, bundle + ".json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Blobs;

        public void Dispose()
        {
            try { AutomationPublishedSource.MakeWritable(Scripts); }
            finally
            {
                AutomationRuntime.DataRootProvider = _previousData;
                RemoteAutomationEnvironment.Restore(_previous);
                _temporary.Dispose();
            }
        }
    }
}
