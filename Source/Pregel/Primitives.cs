using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heron.Pregel;

public readonly record struct NodeId
{
    [JsonConstructor]
    public NodeId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ChannelId
{
    [JsonConstructor]
    public ChannelId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PregelRunId
{
    [JsonConstructor]
    public PregelRunId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PregelCheckpointId
{
    [JsonConstructor]
    public PregelCheckpointId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PregelInvocationId
{
    [JsonConstructor]
    public PregelInvocationId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PregelAttemptId
{
    [JsonConstructor]
    public PregelAttemptId(string value) => Value = PregelId.Validate(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PregelUnit
{
    public static PregelUnit Value { get; } = new();
}

public sealed record PregelEncodedValue(string CodecId, byte[] Data)
{
    public PregelEncodedValue Copy() => new(CodecId, Data.ToArray());
}

public interface ICheckpointCodec<T>
{
    string CodecId { get; }
    byte[] Encode(T value);
    T Decode(ReadOnlySpan<byte> data);
}

public static class CheckpointCodecs
{
    public static ICheckpointCodec<T> Json<T>(string? codecId = null, bool allowNull = false) =>
        new SystemTextJsonCheckpointCodec<T>(codecId ?? $"json:{PregelTypeIdentity.Normalize(typeof(T).FullName ?? typeof(T).Name)}{(allowNull ? ":nullable" : "")}", allowNull);

    public static ICheckpointCodec<int> Int32 { get; } = Json<int>("system:int32:v1");
    public static ICheckpointCodec<long> Int64 { get; } = Json<long>("system:int64:v1");
    public static ICheckpointCodec<double> Double { get; } = Json<double>("system:double:v1");
    public static ICheckpointCodec<bool> Boolean { get; } = Json<bool>("system:boolean:v1");
    public static ICheckpointCodec<string> String { get; } = Json<string>("system:string:v1");
    public static ICheckpointCodec<byte[]> Bytes { get; } = Json<byte[]>("system:bytes:v1");
    public static ICheckpointCodec<PregelUnit> Unit { get; } = Json<PregelUnit>("system:unit:v1");

    internal static ICheckpointCodec<T> Default<T>()
    {
        if (typeof(T) == typeof(int)) return (ICheckpointCodec<T>)(object)Int32;
        if (typeof(T) == typeof(long)) return (ICheckpointCodec<T>)(object)Int64;
        if (typeof(T) == typeof(double)) return (ICheckpointCodec<T>)(object)Double;
        if (typeof(T) == typeof(bool)) return (ICheckpointCodec<T>)(object)Boolean;
        if (typeof(T) == typeof(string)) return (ICheckpointCodec<T>)(object)String;
        if (typeof(T) == typeof(byte[])) return (ICheckpointCodec<T>)(object)Bytes;
        if (typeof(T) == typeof(PregelUnit)) return (ICheckpointCodec<T>)(object)Unit;
        return Json<T>();
    }
}

internal interface IJsonCheckpointCodec { }

internal sealed class SystemTextJsonCheckpointCodec<T>(string codecId, bool allowNull = false)
    : ICheckpointCodec<T>, IJsonCheckpointCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public string CodecId { get; } = PregelId.Validate(codecId, nameof(codecId));

    public byte[] Encode(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public T Decode(ReadOnlySpan<byte> data)
    {
        var value = JsonSerializer.Deserialize<T>(data, Options);
        if (value is null && !allowNull)
            throw new PregelException("codec_null", $"Codec '{CodecId}' decoded a null {typeof(T).Name} value.");
        return value!;
    }
}

public sealed record PregelError(string Code, string Message, IReadOnlyDictionary<string, string>? Details = null);

public sealed class PregelException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal static class PregelId
{
    public static string Validate(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Pregel identifiers cannot contain control characters.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Node IDs and graph fingerprints are derived from type names, so renaming a namespace would orphan saved
/// runs. A product that renames its types maps the new names back here, and old checkpoints keep resuming.
/// </summary>
public static class PregelIdentity
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private static System.Text.RegularExpressions.Regex? _pattern;

    /// <summary>Makes every persisted name starting with a namespace or assembly read as its former name.</summary>
    public static void MapName(string current, string former)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(former);
        lock (Gate)
        {
            var names = new Dictionary<string, string>(_names, StringComparer.Ordinal) { [current] = former };
            var alternatives = names.Keys.OrderByDescending(key => key.Length).ThenBy(key => key, StringComparer.Ordinal)
                .Select(System.Text.RegularExpressions.Regex.Escape);
            _pattern = new($@"(?<![\w.])(?:{string.Join('|', alternatives)})(?!\w)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            _names = names;
        }
    }

    internal static string Apply(string name)
    {
        var pattern = _pattern;
        return pattern is null ? name : pattern.Replace(name, match => _names[match.Value]);
    }
}

/// <summary>Keeps persisted type identities stable across script sessions and framework assembly moves.</summary>
internal static class PregelTypeIdentity
{
    private static readonly System.Text.RegularExpressions.Regex ScriptAssembly = new(
        @"ℛ\*[^,\[\]]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex MovedAssembly = new(
        @"(?<![\w.])(?<type>(?<namespace>Heron\.Pregel\.(?:Tooling|Scripting))\.[\w.+`]+(?:\[[,*]*\])*), Heron\.Pregel(?:\.Automation)?(?=, Version=)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Normalize(string name) => PregelIdentity.Apply(MovedAssembly.Replace(
        ScriptAssembly.Replace(name, "Pregel.Script"), "${type}, ${namespace}"));
    public static string Qualified(Type type) => Normalize(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
}

internal static class PregelHash
{
    public static string Sha256(params string[] parts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }
}

internal sealed class PregelPathComparer : IComparer<IReadOnlyList<string>>
{
    public static PregelPathComparer Instance { get; } = new();

    public int Compare(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var commonLength = Math.Min(left.Count, right.Count);
        for (var index = 0; index < commonLength; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}
