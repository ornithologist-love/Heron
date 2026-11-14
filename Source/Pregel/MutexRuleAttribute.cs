namespace Heron.Pregel;

/// <summary>Resource keys acquired together for each graph execution segment.</summary>
/// <remarks>Keys may contain graph input paths, such as workspace:{Workspace} or machine:{MachineId}.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Method,
    AllowMultiple = true, Inherited = true)]
public sealed class MutexRuleAttribute : Attribute
{
    public MutexRuleAttribute(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0) throw new ArgumentException("At least one mutex key is required.", nameof(keys));
        foreach (var key in keys) ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Keys = Array.AsReadOnly(keys.ToArray());
    }

    public IReadOnlyList<string> Keys { get; }

    /// <summary>Maximum simultaneous runs for each key. One keeps the resource exclusive.</summary>
    public int MaxConcurrency { get; set; } = 1;
}
