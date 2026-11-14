using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>One process-wide policy for both injected ILogger and legacy Serilog callers.</summary>
public sealed class ClientLogPolicy(TimeProvider? clock = null) : ILogEventFilter
{
    private sealed record Rule(LogLevel Level, DateTimeOffset? ExpiresAt);
    private readonly ConcurrentDictionary<string, Rule> _rules = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _minimum = (int)LogLevel.Information;

    public LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimum);
        set { Validate(value); Volatile.Write(ref _minimum, (int)value); }
    }

    /// <summary>Category is an ILogger type/namespace prefix or an exact plugin ID (example.viewer).</summary>
    public void SetOverride(string category, LogLevel level, TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        Validate(level);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        _rules[category] = new(level, duration is { } ttl ? _clock.GetUtcNow() + ttl : null);
    }

    public void ClearOverride(string category) => _rules.TryRemove(category, out _);

    public bool IsEnabled(LogEvent logEvent)
    {
        var source = Text(logEvent, "SourceContext");
        var plugin = Text(logEvent, "PluginId");
        var minimum = MinimumLevel;
        var priority = -1;
        foreach (var (category, rule) in _rules)
        {
            if (rule.ExpiresAt is { } until && _clock.GetUtcNow() >= until) continue;
            // A specific class/namespace overrides its plugin-wide rule.
            var score = source == category || source.StartsWith(category + ".", StringComparison.Ordinal)
                ? category.Length + 1 : plugin == category ? 0 : -1;
            if (score <= priority) continue;
            priority = score;
            minimum = rule.Level;
        }
        return minimum != LogLevel.None && (int)logEvent.Level >= (int)minimum;
    }

    private static string Text(LogEvent evt, string name) =>
        evt.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text } ? text : "";

    private static void Validate(LogLevel level)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
    }
}
