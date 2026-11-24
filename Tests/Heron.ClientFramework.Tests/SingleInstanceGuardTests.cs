using FluentAssertions;
using Heron.ClientFramework.Infrastructure;
using Xunit;

namespace Heron.ClientFramework.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void DebugScopeIsolatesByExecutablePathAndComputesDeterministicIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-single-" + Guid.NewGuid().ToString("N"));
        var exeA = Path.Combine(root, "a", "App.exe");
        var exeB = Path.Combine(root, "b", "App.exe");

        var optA1 = SingleInstanceOptions.Create("TestHost", exeA, root, isolateDebug: true, pidFileName: "app.pid");
        var optA2 = SingleInstanceOptions.Create("TestHost", exeA, root, isolateDebug: true, pidFileName: "app.pid");
        var optB = SingleInstanceOptions.Create("TestHost", exeB, root, isolateDebug: true, pidFileName: "app.pid");
        var release = SingleInstanceOptions.Create("TestHost", exeA, root, isolateDebug: false, pidFileName: "app.pid");

        optA1.InstanceId.Should().Be(optA2.InstanceId);
        optA1.MutexName.Should().Be(optA2.MutexName);
        optA1.ActivationEventName.Should().Be(optA2.ActivationEventName);
        optA1.InstanceId.Should().NotBe(optB.InstanceId);
        optA1.MutexName.Should().NotBe(optB.MutexName);
        optA1.PidFilePath.Should().Contain("debug-instances");

        release.InstanceId.Should().Be("release");
        release.MutexName.Should().Be("TestHost_SingleInstance");
        release.ActivationEventName.Should().Be("TestHost_ActivateInstance");
        release.PidFilePath.Should().Be(Path.Combine(root, "app.pid"));
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void TryAcquireRejectsDuplicatesAndLegacyAliasesAndCleansUpPidFileOnDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "heron-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var pidFile = Path.Combine(root, "host.pid");
            var legacy = new SingleInstanceAlias($"Local\\HeronTestLegacy_{id}", $"Local\\HeronTestLegacyAct_{id}");
            var options = new SingleInstanceOptions(
                "test",
                $"Local\\HeronTestPrimary_{id}",
                $"Local\\HeronTestPrimaryAct_{id}",
                Environment.ProcessPath ?? Path.Combine(root, "Test.exe"),
                pidFile,
                "TestHost",
                false,
                [legacy]);

            SingleInstanceGuard.TryAcquire(options, out var guard).Should().BeTrue();
            guard.Should().NotBeNull();
            File.ReadAllText(pidFile).Should().Be(Environment.ProcessId.ToString());

            // Duplicate primary acquisition is rejected without deleting the running PID file.
            SingleInstanceGuard.TryAcquire(options, out var duplicate).Should().BeFalse();
            duplicate.Should().BeNull();
            File.Exists(pidFile).Should().BeTrue();

            // Another instance holding only the legacy alias also blocks acquisition.
            var reversed = options with
            {
                MutexName = $"Local\\HeronTestOther_{id}",
                LegacyAliases = [new SingleInstanceAlias(options.MutexName, options.ActivationEventName)],
            };
            SingleInstanceGuard.TryAcquire(reversed, out var legacyBlocked).Should().BeFalse();
            legacyBlocked.Should().BeNull();

            guard!.Dispose();
            File.Exists(pidFile).Should().BeFalse();

            // After release, acquisition succeeds again.
            SingleInstanceGuard.TryAcquire(options, out var reacquired).Should().BeTrue();
            reacquired!.Dispose();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Anchor("Docs/client-framework.md")]
    public void ActivationChannelCoalescesEarlySignalsAndDeliversToRegisteredCallback()
    {
        if (!OperatingSystem.IsWindows()) return;

        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "heron-act-" + Guid.NewGuid().ToString("N"));
        var options = new SingleInstanceOptions(
            "test",
            $"Local\\HeronTestActMutex_{id}",
            $"Local\\HeronTestActEvent_{id}",
            Environment.ProcessPath ?? Path.Combine(root, "Test.exe"),
            null,
            "TestHost",
            false);

        SingleInstanceGuard.TryAcquire(options, out var guard).Should().BeTrue();
        using (guard)
        {
            // Signal before callback registration: must remain pending until SetActivationCallback.
            SingleInstanceGuard.TrySignalExisting(options.ActivationEventName).Should().BeTrue();

            using var delivered = new ManualResetEventSlim(false);
            guard!.SetActivationCallback(() => delivered.Set());
            delivered.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();

            SingleInstanceGuard.RequestActivation(options, options.ExecutablePath, 123, _ => true, () => true).Should().BeTrue();
            SingleInstanceGuard.RequestActivation(options, Path.Combine(root, "Other.exe"), 123, _ => true, () => true).Should().BeFalse();
            SingleInstanceGuard.IsMainWindowCandidate("TestHost", "TestHost", hasOwner: false).Should().BeTrue();
            SingleInstanceGuard.IsMainWindowCandidate("TestHost", "TestHost", hasOwner: true).Should().BeFalse();
        }
    }
}
