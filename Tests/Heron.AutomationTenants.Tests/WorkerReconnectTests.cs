using System.Net;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Heron.Pregel.Automation;
using Xunit;

namespace Heron.AutomationTenants.Tests;

[CollectionDefinition("Tenant runtime", DisableParallelization = true)]
public sealed class TenantRuntimeCollection { }

[Collection("Tenant runtime")]
[Anchor("Docs/automation-tenants.md")]
public sealed class WorkerReconnectTests
{
    [Fact]
    public async Task A_lost_log_acknowledgement_survives_worker_restart_without_duplicates_or_gaps()
    {
        using var temporary = new TempDirectory();
        var evidence = Path.Combine(temporary.Path, "evidence");
        Directory.CreateDirectory(evidence);
        var original = Encoding.UTF8.GetBytes("{\"message\":\"pear🍐\"}\r\n{\"message\":\"plum李\"}\n");
        File.WriteAllBytes(Path.Combine(evidence, "events.jsonl"), original);
        var store = new AutomationStore(Path.Combine(temporary.Path, "automation"));
        await using var host = new AutomationHost(store, evidence, true, null, null, null, schedulingEnabled: false);
        using var registration = host.RegisterTemplate(new MockTemplate());
        var task = new AutomationTask { Id = "mock-task", TemplateId = "mock-template", Name = "Mock" };
        host.Save(task);
        await using var peer = new WorkerPeer(task, store, evidence);
        var previous = RemoteAutomationEnvironment.CurrentOrNull;
        RemoteAutomationEnvironment.Configure(new()
        {
            LocalRoot = () => temporary.Path, ScriptsRoot = () => Path.Combine(temporary.Path, "scripts"),
            SharedRoot = () => Path.Combine(temporary.Path, "scripts", "Shared"), ClientInstance = () => "mock",
            AgentConfig = () => new(true, peer.Address), DefaultServerUrl = () => peer.Address,
            ResourceServerUrl = () => null, ResourceDomainDirectory = () => Path.Combine(temporary.Path, "resource-domain"),
            ResourceQueueTimeout = () => null, OfficialSourceUrl = peer.Address + "source/",
            StartProcess = (_, _, _) => throw new InvalidOperationException("An observed run must not start another process"),
            QueryPublishedSource = (_, _, _) => Task.FromResult(new RemoteAutomationPublishedSource("", null))
        });
        try
        {
            using var firstLifetime = new CancellationTokenSource();
            var first = new RemoteAutomationAgent(host, new(peer.Address, "mock", "missing-cli"), RemoteAutomationEnvironment.Current)
                .RunAsync(firstLifetime.Token);
            await peer.LostAcknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(20));
            firstLifetime.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(10));

            using var secondLifetime = new CancellationTokenSource();
            var second = new RemoteAutomationAgent(host, new(peer.Address, "mock", "missing-cli"), RemoteAutomationEnvironment.Current)
                .RunAsync(secondLifetime.Token);
            await peer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            secondLifetime.Cancel();
            await second.WaitAsync(TimeSpan.FromSeconds(10));

            peer.Received.ToArray().Should().Equal(original);
            peer.UploadOffsets.Count.Should().BeGreaterThanOrEqualTo(2);
            peer.UploadOffsets.Take(2).Should().OnlyContain(offset => offset == 0, "the first acknowledgement was lost before persistence");
        }
        finally { RemoteAutomationEnvironment.Restore(previous); }
    }

    private sealed class MockInput { public MockInput() { } }
    [TemplateId("mock-template"), DisplayName("Mock")]
    private sealed class MockTemplate : AutomationTemplate<MockInput>
    {
        protected override IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, MockInput input) => [];
        protected override Task<AutomationRunResult> RunAsync(AutomationRunContext context, MockInput input, CancellationToken token) =>
            throw new InvalidOperationException("The observation fixture must never execute");
    }

    private sealed class WorkerPeer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;
        private readonly AutomationTask _task;
        private readonly AutomationStore _store;
        private readonly string _evidence;
        private bool _created;
        public string Address { get; } = LocalEndpoint.Address();
        public MemoryStream Received { get; } = new();
        public List<long> UploadOffsets { get; } = [];
        public TaskCompletionSource LostAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkerPeer(AutomationTask task, AutomationStore store, string evidence)
        {
            _task = task; _store = store; _evidence = evidence;
            _listener.Prefixes.Add(Address); _listener.Start();
            _loop = Listen();
        }

        private async Task Listen()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                    await Handle(context);
                }
            }
            catch (Exception error) when (_lifetime.IsCancellationRequested && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }

        private async Task Handle(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url!.AbsolutePath;
                object response;
                if (path == "/source/catalog.json") response = new
                {
                    shared = new { id = "Shared", revision = "1", sha256 = new string('0', 64), archive = "Shared.zip" },
                    scripts = Array.Empty<object>()
                };
                else if (path.EndsWith("/poll", StringComparison.Ordinal))
                {
                    if (!_created)
                    {
                        _created = true;
                        _store.Upsert(new()
                        {
                            TaskId = _task.Id, RunId = "mock-local-run", TaskSnapshot = _task,
                            QueuedAt = DateTimeOffset.UtcNow, Status = AutomationRunStatus.Succeeded,
                            Revision = 1, EvidenceDirectories = [_evidence]
                        });
                    }
                    response = new { task_catalog = new[] { new { id = _task.Id, name = "Mock", definition_ids = new[] { _task.TemplateId } } } };
                }
                else
                {
                    using var body = await JsonDocument.ParseAsync(context.Request.InputStream);
                    if (path.EndsWith("/observations", StringComparison.Ordinal)) response = new { id = "mock-remote-run" };
                    else if (path.EndsWith("/logs", StringComparison.Ordinal))
                    {
                        var offset = body.RootElement.GetProperty("offset").GetInt64();
                        UploadOffsets.Add(offset);
                        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', body.RootElement.GetProperty("lines")
                            .EnumerateArray().Select(line => line.GetString())) + "\n");
                        if (offset == Received.Length) Received.Write(bytes);
                        if (!LostAcknowledgement.Task.IsCompleted)
                        {
                            context.Response.Abort();
                            LostAcknowledgement.TrySetResult();
                            return;
                        }
                        response = new { next_offset = Received.Length };
                    }
                    else
                    {
                        if (body.RootElement.TryGetProperty("logs_complete", out var complete) && complete.GetBoolean()) Completed.TrySetResult();
                        response = new { };
                    }
                }
                var result = JsonSerializer.SerializeToUtf8Bytes(response);
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = result.Length;
                await context.Response.OutputStream.WriteAsync(result);
            }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); _listener.Close();
            await _loop;
            Received.Dispose(); _lifetime.Dispose();
        }
    }
}
