using System.Text.Json;

namespace Heron.Core.Config;

/// <summary>A subsystem owns its schema, defaults, compatibility and validation.</summary>
public interface IConfigurationModule<out T>
{
    T Read(ConfigurationContext context);
}

/// <summary>One document snapshot and its base directory, shared by all sections in a module read.</summary>
public sealed class ConfigurationContext(string baseDirectory, JsonElement? document)
{
    public string BaseDirectory { get; } = baseDirectory;

    public T Read<T>(IConfigurationModule<T> module) => module.Read(this);

    public JsonElement? Section(string name) => document is { ValueKind: JsonValueKind.Object } root
        && root.TryGetProperty(name, out var section) ? section : null;

    public T? ReadSection<T>(string name, JsonSerializerOptions? options = null) =>
        Section(name) is { } section ? section.Deserialize<T>(options) : default;

    public string ResolvePath(string path) => Path.GetFullPath(Path.IsPathRooted(path)
        ? path : Path.Combine(BaseDirectory, path));
}
