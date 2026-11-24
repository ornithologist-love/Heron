using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Plugins;
using Xunit;

namespace Heron.ClientFramework.Tests;

public sealed class ClientFrameworkL2Tests
{
    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void Install_root_holders_parses_json_and_excludes_query_pid()
    {
        const string json = """
        {
          "processes": [
            { "pid": 123, "name": "FileLocksmithCLI.exe", "files": ["C:\\App\\foo.dll"] },
            { "pid": 456, "name": "Code.exe", "files": ["C:\\App\\bar.dll", "C:\\App\\baz.dll"] }
          ]
        }
        """;

        var holders = InstallRootHolders.ParseFileLocksmithJson(json, excludePid: 123);
        holders.Should().ContainSingle().Which.Should().Be(new InstallRootHolder(456, "Code.exe", "C:\\App\\bar.dll"));

        Assert.Throws<JsonException>(() => InstallRootHolders.ParseFileLocksmithJson("{}", 0));
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public async Task Velopack_updater_parses_versions_and_checks_http_fallback()
    {
        VelopackClientUpdater.TryParseVersion("v2.3.93-agent.1+abc", out var v).Should().BeTrue();
        v.Should().Be(new Version(2, 3, 93));

        var payload = """{"version":"2.5.0","downloadUrl":"/updates/Setup.exe","fileSize":1024,"releaseNotes":"notes"}""";
        using var http = new HttpClient(new StubHandler(payload)) { BaseAddress = new Uri("https://updates.example/") };

        using var skipped = new VelopackClientUpdater(new ClientUpdateOptions
        {
            FeedUrl = "https://updates.example/updates",
            InstallerDownloadUrl = "https://updates.example/Setup.exe",
            LatestVersionApiPath = "api/version/latest",
            GetCurrentVersion = () => "2.4.0",
            SkipCheck = true,
            HttpClient = http,
        });
        (await skipped.CheckForUpdateAsync()).Should().BeNull();

        using var updater = new VelopackClientUpdater(new ClientUpdateOptions
        {
            FeedUrl = "https://updates.example/updates",
            InstallerDownloadUrl = "https://updates.example/Setup.exe",
            LatestVersionApiPath = "api/version/latest",
            GetCurrentVersion = () => "2.4.0",
            HttpClient = http,
        });
        var newer = await updater.CheckForUpdateAsync();
        newer.Should().NotBeNull();
        newer!.Version.Should().Be("2.5.0");
        newer.DownloadUrl.Should().Be("https://updates.example/updates/Setup.exe");
        newer.FileSize.Should().Be(1024);
        newer.IsAutomaticUpdate.Should().BeFalse();

        (await updater.CheckForUpdateAsync("2.5.0")).Should().BeNull();
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void Product_assembly_cli_parses_options_and_builds_mac_plist()
    {
        ClientProductAssemblyCli.NormalizeTargetPlatform("win-x64").Should().Be("Win64");
        ClientProductAssemblyCli.NormalizeTargetPlatform("osx-arm64").Should().Be("Mac");
        ClientProductAssemblyCli.NormalizeTargetPlatform("linux-x64").Should().Be("Linux");

        var parsed = ClientProductAssemblyCli.ParseOptions([
            "--repository", "R",
            "--edition", "E",
            "--plugin-package", "P1",
            "--plugin-package", "P2",
        ]);
        parsed["--plugin-package"].Should().Equal("P1", "P2");

        Assert.Throws<ArgumentException>(() => ClientProductAssemblyCli.ParseOptions(["--edition", "A", "--edition", "B"]));

        var options = new MacDevelopmentBundleOptions(
            "com.example.debug.",
            "Example",
            "Example",
            "Example.Client",
            "Example.icns");
        var plistA = MacDevelopmentBundle.BuildInfoPlist("/tmp/out", options);
        var plistB = MacDevelopmentBundle.BuildInfoPlist("/tmp/out", options);
        plistA.Should().Be(plistB);
        plistA.Should().Contain("com.example.debug.");
        plistA.Should().Contain("<string>Example.icns</string>");
        using var plistBytes = new MemoryStream(Encoding.UTF8.GetBytes(plistA));
        XDocument.Load(plistBytes).Root!.Name.LocalName.Should().Be("plist");
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
