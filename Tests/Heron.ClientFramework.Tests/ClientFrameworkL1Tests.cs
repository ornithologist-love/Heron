using System.Text.Json;
using FluentAssertions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Plugins;
using Heron.ClientFramework.Runtime;
using Heron.ClientFramework.Shell;
using Xunit;

namespace Heron.ClientFramework.Tests;

public sealed class ClientFrameworkL1Tests
{
    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void WebView2_process_lifetime_discovers_only_owned_descendant_browsers()
    {
        var snapshot = new WebView2ProcessLifetime.ProcessEntry[]
        {
            new(100, 1, "Host.exe"),
            new(200, 100, "msedgewebview2.exe"),
            new(201, 200, "msedgewebview2.exe"),
            new(202, 100, "helper.exe"),
            new(203, 202, "msedgewebview2.exe"),
            new(999, 500, "msedgewebview2.exe"),
        };

        var descendants = WebView2ProcessLifetime.FindDescendantProcessIds([100], snapshot);
        descendants.Should().BeEquivalentTo([200, 201, 202, 203]);

        var webviews = WebView2ProcessLifetime.FindOwnedWebViewProcessIds(100, snapshot);
        webviews.Should().BeEquivalentTo([200, 201, 203]);
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void Client_smoke_run_parses_argument_and_writes_ready_and_failure_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resultPath = Path.Combine(root, "nested", "smoke.json");
            ClientSmokeRun.ReadResultPath(["--foo", "--smoke-result", resultPath]).Should().Be(Path.GetFullPath(resultPath));
            ClientSmokeRun.ReadResultPath(["--smoke-result"]).Should().BeNull();

            var plugins = new[]
            {
                new PluginStatus("heron.alpha", PluginState.Running, null, "1.0.0", DisplayName: "Alpha") { Description = "desc" },
            };
            ClientSmokeRun.WriteReady("local", null, "recovered", plugins, ["alpha.main"], resultPath);
            using (var doc = JsonDocument.Parse(File.ReadAllText(resultPath)))
            {
                doc.RootElement.GetProperty("ready").GetBoolean().Should().BeTrue();
                doc.RootElement.GetProperty("edition").GetString().Should().Be("local");
                doc.RootElement.GetProperty("recovery_message").GetString().Should().Be("recovered");
                doc.RootElement.GetProperty("plugins").GetArrayLength().Should().Be(1);
                doc.RootElement.GetProperty("pages").EnumerateArray().Select(e => e.GetString()).Should().Equal("alpha.main");
            }

            ClientSmokeRun.WriteStartupFailure(new InvalidOperationException("boom"), resultPath);
            using (var doc = JsonDocument.Parse(File.ReadAllText(resultPath)))
            {
                doc.RootElement.GetProperty("ready").GetBoolean().Should().BeFalse();
                doc.RootElement.GetProperty("startup_error").GetString().Should().Contain("boom");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void Machine_resources_formatting_handles_nvidia_dxgi_and_disks()
    {
        MachineResourcesStatusViewModel.FormatVramLabel(new GpuMemorySnapshot("RTX", 16, 12.5))
            .Should().Be("VRAM 12.5/16 GB 可用");
        MachineResourcesStatusViewModel.FormatVramLabel(new GpuMemorySnapshot("Intel", 8, null))
            .Should().Be("VRAM 8 GB");
        MachineResourcesStatusViewModel.FormatVramLabel(GpuMemorySnapshot.Unknown)
            .Should().Be("VRAM 未知");

        MachineResourcesStatusViewModel.FormatDiskSummary([])
            .Should().Be("未知");
        MachineResourcesStatusViewModel.FormatDiskSummary([new("C:", 500, 120.5), new("D:", 500, 200)])
            .Should().Be("320.5/1000 GB 可用");
        MachineResourcesStatusViewModel.FormatStatusText("内存 16/32 GB 可用")
            .Should().Be("16 GB / 32 GB");
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void Plugins_page_view_model_applies_filter_and_empty_workspace_handles_error()
    {
        var statuses = new[]
        {
            new PluginStatus("visible.one", PluginState.Running, null, "1.0.0", DisplayName: "Visible") { Description = "Visible plugin" },
            new PluginStatus("hidden.one", PluginState.Running, null, "1.0.0", DisplayName: "Hidden") { Description = "Hidden plugin" },
        };
        var vm = new PluginsPageViewModel(
            () => statuses,
            () => null,
            () => null,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            filter: status => status.Id != "hidden.one");

        vm.Refresh();
        vm.Plugins.Select(row => row.Id).Should().Equal("visible.one");

        var emptyOk = new EmptyWorkspacePageViewModel();
        emptyOk.HasError.Should().BeFalse();

        var emptyErr = new EmptyWorkspacePageViewModel("plugin load failed");
        emptyErr.HasError.Should().BeTrue();
        emptyErr.Message.Should().Be("plugin load failed");
    }
}
