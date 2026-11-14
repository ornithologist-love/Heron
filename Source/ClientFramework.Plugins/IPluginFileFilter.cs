namespace Heron.ClientFramework.Plugins;

/// <summary>Consumer-owned payload selection, shared by copying and cache validation.</summary>
public interface IPluginFileFilter
{
    /// <summary>Changes whenever the selection rules change.</summary>
    string Fingerprint { get; }
    bool Include(string relativePath);
}
