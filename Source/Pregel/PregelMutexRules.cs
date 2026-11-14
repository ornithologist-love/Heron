using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Heron.Pregel;

public sealed record PregelMutexRule(string Key, int MaxConcurrency = 1);

internal static class PregelMutexRules
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\}");

    public static PregelMutexRule[] Read(Type owner, string? memberName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        MemberInfo declaration = owner;
        if (memberName is not null)
        {
            declaration = owner.GetMember(memberName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).SingleOrDefault()
                ?? throw new ArgumentException($"Mutex rule declaration not found: {owner.FullName}.{memberName}", nameof(memberName));
        }
        var rules = Normalize(declaration.GetCustomAttributes<MutexRuleAttribute>(inherit: true)
            .SelectMany(rule => rule.Keys.Select(key => new PregelMutexRule(key, rule.MaxConcurrency))));
        foreach (var rule in rules)
        {
            var literal = Placeholder.Replace(rule.Key, "");
            if (literal.Contains('{') || literal.Contains('}'))
                throw new ArgumentException($"Invalid mutex key pattern: {rule.Key}");
        }
        return rules;
    }

    public static PregelMutexRule[] Resolve(CompiledPregelGraph graph, PregelCheckpoint checkpoint) =>
        Normalize(graph.Definition.MutexRules.Select(rule => new PregelMutexRule(Placeholder.Replace(rule, match =>
        {
            var path = match.Groups[1].Value.Split('.');
            var variable = graph.Variables.FirstOrDefault(pair => pair.Value == path[0]);
            var offset = 1;
            if (variable.Value is null)
            {
                variable = graph.Variables.FirstOrDefault(pair => pair.Value == "input");
                offset = 0;
            }
            if (variable.Value is null)
                throw new PregelException("mutex_input_missing", $"Mutex key '{rule}' requires graph input '{path[0]}'.");
            object? value = graph.Channels[variable.Key].DecodeState(checkpoint.ChannelValues[variable.Key]);
            for (var i = offset; i < path.Length; i++)
            {
                var property = value?.GetType().GetProperty(path[i], BindingFlags.Public | BindingFlags.Instance);
                if (property is null || property.GetIndexParameters().Length != 0)
                    throw new PregelException("mutex_input_missing", $"Mutex input path '{match.Groups[1].Value}' does not exist.");
                value = property.GetValue(value);
            }
            var text = value is string or ValueType ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            if (string.IsNullOrWhiteSpace(text))
                throw new PregelException("mutex_input_invalid", $"Mutex input '{match.Groups[1].Value}' must be a nonempty scalar value.");
            return text;
        }), graph.Definition.MutexLimits.GetValueOrDefault(rule, 1))));

    public static PregelMutexRule[] Restore(PregelCheckpoint checkpoint) => Normalize(checkpoint.MutexKeys
        .Select(key => new PregelMutexRule(key, checkpoint.MutexLimits.GetValueOrDefault(key, 1))));

    // The default exclusive limit is implicit; keys remain the resource identities in graph/checkpoint data.
    public static IReadOnlyDictionary<string, int> Limits(IEnumerable<PregelMutexRule> rules) => rules
        .Where(rule => rule.MaxConcurrency != 1)
        .ToFrozenDictionary(rule => rule.Key, rule => rule.MaxConcurrency, StringComparer.OrdinalIgnoreCase);

    internal static PregelMutexRule[] Normalize(IEnumerable<PregelMutexRule> rules)
    {
        var unique = new Dictionary<string, PregelMutexRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Key);
            if (rule.MaxConcurrency < 1)
                throw new PregelException("mutex_limit_invalid", $"Mutex resource '{rule.Key}' must have MaxConcurrency >= 1.");
            if (unique.TryGetValue(rule.Key, out var existing) && existing.MaxConcurrency != rule.MaxConcurrency)
                throw new PregelException("mutex_limit_conflict",
                    $"Mutex resource '{rule.Key}' has conflicting limits: {existing.MaxConcurrency} and {rule.MaxConcurrency}.");
            unique.TryAdd(rule.Key, rule);
        }
        return unique.Values.OrderBy(rule => rule.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
