using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Heron.Pregel;

/// <summary>Renders initial JSON input strings against the same run's input snapshot.</summary>
internal static class PregelInputTemplates
{
    private static readonly Regex Placeholder = new(
        @"\$\{\{\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*\}\}",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyDictionary<ChannelId, PregelEncodedValue> Bind(
        Dictionary<ChannelId, PregelEncodedValue> values,
        IReadOnlyDictionary<ChannelId, string> names,
        IEnumerable<ChannelId> jsonInputs)
    {
        var channels = jsonInputs.ToArray();
        if (!channels.Any(id => Encoding.UTF8.GetString(values[id].Data).Contains("${{", StringComparison.Ordinal)))
            return values;
        var roots = channels.ToDictionary(id => names[id], id => JsonNode.Parse(values[id].Data), StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);

        string Resolve(Match match)
        {
            var path = match.Groups[1].Value.Split('.');
            var root = roots.ContainsKey(path[0]) ? path[0] : "input";
            var offset = root == path[0] ? 1 : 0;
            var key = string.Join(".", new[] { root }.Concat(path.Skip(offset)));
            if (resolved.TryGetValue(key, out var cached)) return cached;
            if (!roots.TryGetValue(root, out var node))
                throw new PregelException("input_variable_missing", $"Input variable '{match.Groups[1].Value}' does not exist.");
            for (var i = offset; i < path.Length; i++)
            {
                if (node is not JsonObject owner || !owner.TryGetPropertyValue(path[i], out node))
                    throw new PregelException("input_variable_missing", $"Input variable '{match.Groups[1].Value}' does not exist.");
            }
            if (node is not JsonValue value)
                throw new PregelException("input_variable_invalid", $"Input variable '{match.Groups[1].Value}' must be a non-null scalar.");
            if (!resolving.Add(key))
                throw new PregelException("input_variable_cycle", $"Input variable '{match.Groups[1].Value}' has a circular reference.");
            try
            {
                var text = value.TryGetValue<string>(out var literal)
                    ? Placeholder.Replace(literal!, Resolve) : value.ToJsonString();
                resolved.Add(key, text);
                return text;
            }
            finally { resolving.Remove(key); }
        }

        JsonNode? Render(JsonNode? node) => node switch
        {
            JsonObject owner => new JsonObject(owner.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, Render(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Render).ToArray()),
            JsonValue value when value.TryGetValue<string>(out var text)
                => JsonValue.Create(Placeholder.Replace(text!, Resolve)),
            _ => node?.DeepClone()
        };

        // Keep the source roots immutable, so field order cannot affect substitutions.
        foreach (var id in channels)
        {
            if (Encoding.UTF8.GetString(values[id].Data).Contains("${{", StringComparison.Ordinal))
                values[id] = values[id] with { Data = JsonSerializer.SerializeToUtf8Bytes(Render(roots[names[id]])) };
        }
        return values;
    }
}
