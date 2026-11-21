using System.Text;
using System.Text.Json;
using FluentAssertions;
using Heron.AutomationTenants;
using Heron.Pregel;
using Graph = Heron.Pregel.Pregel;
using Xunit;

namespace Heron.AutomationTenants.Tests;

[Anchor("Docs/automation-tenants.md")]
public sealed class ProtocolBoundaryTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Log_cursor_counts_original_utf8_bytes_and_holds_a_partial_tail(string newline)
    {
        using var temporary = new TempDirectory();
        var first = "{\"fruit\":\"梨🍐\"}" + newline;
        var second = "{\"fruit\":\"李\"}" + newline;
        var partial = "{\"fruit\":\"未完";
        var path = Path.Combine(temporary.Path, "events.jsonl");
        File.WriteAllText(path, first + second + partial, new UTF8Encoding(false));

        var batch = Utf8EventLog.Read(path, 0);
        var acknowledged = Encoding.UTF8.GetBytes(string.Join('\n', batch.Lines) + "\n");
        acknowledged.Should().Equal(Encoding.UTF8.GetBytes(first + second));
        Utf8EventLog.Read(path, acknowledged.Length).Lines.Should().BeEmpty();

        File.AppendAllText(path, "\"}" + newline, new UTF8Encoding(false));
        var resumed = Utf8EventLog.Read(path, acknowledged.Length);
        var remaining = Encoding.UTF8.GetBytes(string.Join('\n', resumed.Lines) + "\n");
        acknowledged.Concat(remaining).Should().Equal(File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("child")]
    [InlineData("path")]
    [InlineData("codec")]
    public void Subgraphs_reject_a_different_root_path_fingerprint_or_input_codec(string changed)
    {
        var (root, request) = Invocation();
        var invalid = changed switch
        {
            "root" => request with { RootGraphFingerprint = "unregistered-root" },
            "child" => request with { GraphFingerprint = "another-child" },
            "path" => request with { MutationPath = ["unregistered-child"] },
            _ => request with { Input = request.Input with { CodecId = "another-codec" } }
        };
        var resolve = () => PregelSubgraphs.Resolve(root, invalid);
        resolve.Should().Throw<PregelException>();
    }

    [Fact]
    public void A_source_path_in_the_wire_payload_does_not_select_executable_code()
    {
        var (root, request) = Invocation();
        var node = JsonSerializer.SerializeToNode(request)!.AsObject();
        node["script_path"] = "unregistered/foreign.csx";
        var received = node.Deserialize<PregelSubgraphExecutionRequest>()!;
        var resolved = PregelSubgraphs.Resolve(root, received);
        resolved.Should().NotBeNull();
        received.RootGraphFingerprint.Should().Be(root.Definition.Fingerprint);
    }

    private static (CompiledPregelGraph Root, PregelSubgraphExecutionRequest Request) Invocation()
    {
        var child = Graph.Define<MockInput>("mock-child").Then(Transform).Compile();
        var root = Graph.Define<MockInput>("mock-root").Then(Pass)
            .Graph("branch", child, target: _ => "mock-machine").Compile().Compiled;
        var mutation = root.Definition.Mutations.Single();
        var codec = CheckpointCodecs.Json<MockInput>();
        return (root, new("mock-parent", mutation.Entry.Value, "mock-invocation", "mock-child-run", [mutation.Id],
            new(codec.CodecId, codec.Encode(new())), "mock-machine", root.Definition.Fingerprint, mutation.Template.Fingerprint));
    }

    private static void Pass(MockInput input, PregelNodeContext context, CancellationToken token) { }
    private static string Transform(MockInput input, PregelNodeContext context, CancellationToken token) => input.Value.ToUpperInvariant();
    private sealed class MockInput { public string Value { get; init; } = "apricot"; }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "heron-tenants-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
