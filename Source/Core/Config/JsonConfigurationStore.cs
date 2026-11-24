using System.Text.Json;

namespace Heron.Core.Config;

/// <summary>Thread-safe JSON section cache with no application-specific paths or schema.</summary>
public sealed class JsonConfigurationStore
{
    private readonly object _gate = new();
    private JsonElement? _root;
    private string? _cachedPath;

    public void Reset()
    {
        lock (_gate)
        {
            _root = null;
            _cachedPath = null;
        }
    }

    public JsonElement? LoadSection(string? filePath, string sectionName)
    {
        var root = LoadDocument(filePath);
        return root is { } value && value.TryGetProperty(sectionName, out var section) ? section : null;
    }

    public JsonElement? LoadDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        lock (_gate)
        {
            if (_root == null || !string.Equals(_cachedPath, filePath, StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(filePath));
                    _root = document.RootElement.Clone();
                    _cachedPath = filePath;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    return null;
                }
            }

            return _root;
        }
    }
}
