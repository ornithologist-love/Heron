using System.Diagnostics;
using FluentAssertions;
using Heron.Core.Config;
using Heron.Core.Process;
using Heron.Core.Scheduling;
using Heron.Core.Utils;
using Xunit;

namespace Heron.ClientFramework.Tests;

public sealed class HeronCoreTests
{
    private sealed record SampleSettings(string Endpoint);

    private sealed class SampleModule : IConfigurationModule<SampleSettings>
    {
        public SampleSettings Read(ConfigurationContext context)
        {
            var value = context.ReadSection<SampleSettings>("sample");
            return value ?? new SampleSettings(context.ResolvePath("default"));
        }
    }

    [Fact]
    [Anchor("Docs/core.md")]
    public void FnMatch_matches_wildcards_case_insensitively()
    {
        FnMatch.Match("Content/Maps/MainLevel.umap", "*.umap").Should().BeTrue();
        FnMatch.Match("Content/Maps/MainLevel.UMAP", "*level?.umap").Should().BeFalse();
        FnMatch.Match("LevelA.txt", "level?.txt").Should().BeTrue();
    }

    [Fact]
    [Anchor("Docs/core.md")]
    public void PathSafety_neutralizes_traversal_and_invalid_characters()
    {
        PathSafety.SanitizePathSegment("../../secret:name").Should().Be("____secret_name");
        PathSafety.SanitizePathSegment("   ...   ").Should().Be("_");
        PathSafety.SanitizePathSegment("   .   ", "fallback").Should().Be("fallback");
        PathSafety.SanitizeFileName(@"C:\dir\sub\report?.json").Should().Be("report_.json");
    }

    [Fact]
    [Anchor("Docs/core.md")]
    public void DailyTaskRunner_computes_next_run_today_or_tomorrow()
    {
        var morning = new DateTime(2026, 11, 25, 8, 30, 0);
        DailyTaskRunner.ComputeNextRun(morning, 9, 0).Should().Be(new DateTime(2026, 11, 25, 9, 0, 0));
        DailyTaskRunner.ComputeNextRun(morning, 8, 30).Should().Be(new DateTime(2026, 11, 26, 8, 30, 0));
        DailyTaskRunner.ComputeNextRun(morning, 7, 0).Should().Be(new DateTime(2026, 11, 26, 7, 0, 0));
    }

    [Fact]
    [Anchor("Docs/core.md")]
    public void JsonConfigurationStore_loads_sections_and_handles_missing_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-core-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "config.json");
            File.WriteAllText(file, """{"sample":{"Endpoint":"https://api.example"}}""");

            var store = new JsonConfigurationStore();
            var doc = store.LoadDocument(file);
            var settings = new SampleModule().Read(new ConfigurationContext(root, doc));
            settings.Endpoint.Should().Be("https://api.example");

            store.LoadSection(Path.Combine(root, "missing.json"), "sample").Should().BeNull();
            var fallback = new SampleModule().Read(new ConfigurationContext(root, null));
            fallback.Endpoint.Should().Be(Path.Combine(root, "default"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Anchor("Docs/core.md")]
    public void OwnedProcessRegistry_registers_and_snapshots_running_process()
    {
        using var current = Process.GetCurrentProcess();
        OwnedProcessRegistry.Register(current, OwnedProcessDomains.General, stopOnHostExit: false);
        OwnedProcessRegistry.Snapshot(OwnedProcessDomains.General)
            .Should().Contain(entry => entry.ProcessId == current.Id && !entry.StopOnHostExit);
    }
}
