using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using FluentAssertions;
using Heron.Pregel;
using Xunit;

namespace Heron.AutomationTenants.Tests;

[Anchor("Docs/automation-tenants.md")]
public sealed class ResourceRecoveryTests
{
    [Fact]
    public async Task Restart_restores_an_existing_grant_before_admitting_competing_work()
    {
        using var temporary = new TempDirectory();
        var address = LocalEndpoint.Address();
        using var http = new HttpClient { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(10) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = new PregelResourceRequest("mock-first", "run-first", [new("mock-slot", 1)], null, null);
        var second = new PregelResourceRequest("mock-second", "run-second", [new("mock-slot", 1)], null, null);
        PregelResourceContext? original;
        await using (var server = await PregelResourceServer.StartAsync(address, "mock-tenant", temporary.Path, deadline.Token))
        {
            await Send("acquire", new() { Request = first });
            original = (await Granted(first.RequestId)).Context;
        }

        await using (var restarted = await PregelResourceServer.StartAsync(address, "mock-tenant", temporary.Path, deadline.Token))
        {
            var restored = await Send("state", new() { RequestId = first.RequestId });
            restored.State.Should().Be("granted");
            restored.Context.Should().Be(original);
            var contender = await Send("acquire", new() { Request = second });
            contender.State.Should().Be("pending", "the recovered grant still consumes the resource capacity");
            await Send("release", new() { RequestId = first.RequestId, Context = original });
            (await Granted(second.RequestId)).Context.Should().NotBeNull();
        }

        async Task<PregelResourceReply> Send(string action, PregelResourceMessage message)
        {
            using var response = await http.PostAsJsonAsync("resources/" + action,
                message with { TenantId = "mock-tenant", MachineName = "mock-machine" },
                RemotePregelResourceCoordinator.Json, deadline.Token);
            response.EnsureSuccessStatusCode();
            var reply = (await response.Content.ReadFromJsonAsync<PregelResourceReply>(RemotePregelResourceCoordinator.Json, deadline.Token))!;
            reply.Error.Should().BeNull();
            return reply;
        }

        async Task<PregelResourceReply> Granted(string id)
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var reply = await Send("state", new() { RequestId = id });
                if (reply.State == "granted") return reply;
                await Task.Yield();
            }
        }
    }
}

internal static class LocalEndpoint
{
    internal static string Address()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
    }
}
