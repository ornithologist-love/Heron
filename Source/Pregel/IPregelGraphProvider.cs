namespace Heron.Pregel;

/// <summary>Exposes named, compiled graphs to hosts without executing their nodes.</summary>
public interface IPregelGraphProvider
{
    IReadOnlyDictionary<string, object> Graphs { get; }
}
