using System.Collections.ObjectModel;
using System.Text.Json;

namespace Heron.Pregel;

public sealed record PregelParameterChoice(JsonElement Value, string Label);
public sealed record PregelInputParameter(string Name, JsonElement Value, IReadOnlyList<PregelParameterChoice>? With);

/// <summary>Normalizes input values and optional parameter declarations without performing IO or variable rendering.</summary>
public sealed class PregelInputSchema
{
    private PregelInputSchema(JsonElement values, Dictionary<string, PregelInputParameter> parameters)
    {
        Values = values;
        With = new ReadOnlyDictionary<string, PregelInputParameter>(parameters);
    }

    public JsonElement Values { get; }
    public IReadOnlyDictionary<string, PregelInputParameter> With { get; }

    public static PregelInputSchema Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var parameters = new Dictionary<string, PregelInputParameter>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return new(root.Clone(), parameters);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement declarations = default;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "with")
            {
                if (declarations.ValueKind != JsonValueKind.Undefined)
                    throw new JsonException("Duplicate with declaration.");
                declarations = property.Value;
            }
            else if (!values.TryAdd(property.Name, property.Value.Clone()))
                throw new JsonException($"Duplicate input '{property.Name}'.");
        }
        if (declarations.ValueKind != JsonValueKind.Undefined)
        {
            if (declarations.ValueKind != JsonValueKind.Array)
                throw new JsonException("with must be an array of parameter declarations.");
            foreach (var declaration in declarations.EnumerateArray())
            {
                if (declaration.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Each parameter must be an object containing its name/default and optional value list.");
                var properties = declaration.EnumerateObject().ToArray();
                var inputs = properties.Where(property => property.Name != "value").ToArray();
                if (inputs.Length != 1 || string.IsNullOrWhiteSpace(inputs[0].Name)
                    || inputs[0].Name == "with" || properties.Count(property => property.Name == "value") > 1)
                    throw new JsonException("Each parameter must declare exactly one name/default and at most one value list.");
                var input = inputs[0];
                var defaults = input.Value.ValueKind == JsonValueKind.Array
                    ? input.Value.EnumerateArray().ToArray() : new[] { input.Value };
                foreach (var value in defaults) RequireScalar(value, input.Name);
                if (defaults.Skip(1).Any(value => !SameType(value, defaults[0])))
                    throw new JsonException($"Parameter '{input.Name}' requires values of the same type.");
                if (defaults.Select(ScalarText).Distinct(StringComparer.Ordinal).Count() != defaults.Length)
                    throw new JsonException($"Parameter '{input.Name}' contains duplicate default values.");
                if (!values.TryAdd(input.Name, input.Value.Clone()))
                    throw new JsonException($"Duplicate input '{input.Name}'.");
                IReadOnlyList<PregelParameterChoice>? choices = null;
                if (declaration.TryGetProperty("value", out var list))
                {
                    if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                        throw new JsonException($"Parameter '{input.Name}' requires a non-empty value array.");
                    var items = new List<PregelParameterChoice>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in list.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var value)
                            || !item.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(label.GetString()))
                            throw new JsonException($"Choices for '{input.Name}' require value and label.");
                        RequireScalar(value, input.Name);
                        if (defaults.Any(selected => !SameType(value, selected))
                            || items.Any(previous => !SameType(value, previous.Value)))
                            throw new JsonException($"Choice values for '{input.Name}' must match the default value's type.");
                        if (!seen.Add(value.ValueKind + ":" + ScalarText(value)))
                            throw new JsonException($"Duplicate choice for '{input.Name}'.");
                        items.Add(new(value.Clone(), label.GetString()!));
                    }
                    if (defaults.Any(selected => !items.Any(item => ScalarText(item.Value) == ScalarText(selected))))
                        throw new JsonException($"Default value for '{input.Name}' is missing from its value list.");
                    choices = items.AsReadOnly();
                }
                parameters.Add(input.Name, new(input.Name, input.Value.Clone(), choices));
            }
        }
        return new(JsonSerializer.SerializeToElement(values), parameters);
    }

    public static string ScalarText(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : value.GetRawText();

    private static bool IsBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool SameType(JsonElement first, JsonElement second) => first.ValueKind == second.ValueKind
        || IsBoolean(first) && IsBoolean(second);

    private static void RequireScalar(JsonElement value, string name)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"Parameter '{name}' must have a string, number or boolean value.");
    }
}
