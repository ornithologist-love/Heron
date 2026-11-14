using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

/// <summary>User choices, independent of the version activation/rollback journal.</summary>
public sealed class PluginEnablementStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private string ChoicesPath => Path.Combine(Path.GetDirectoryName(_path)!, "preferences.json");

    private Dictionary<string, bool> ReadChoices()
    {
        if (!File.Exists(ChoicesPath))
            return Read().ToDictionary(id => id, _ => false, StringComparer.OrdinalIgnoreCase);
        var choices = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(ChoicesPath));
        if (choices == null || choices.Keys.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid plugin enablement preferences.");
        return new(choices, StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> Read(IEnumerable<string> disabledByDefault) => ApplyChoices(disabledByDefault, ReadChoices());

    private static HashSet<string> ApplyChoices(IEnumerable<string> disabledByDefault, Dictionary<string, bool> choices)
    {
        var disabled = new HashSet<string>(disabledByDefault, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, enabled) in choices)
            if (enabled) disabled.Remove(id); else disabled.Add(id);
        return disabled;
    }

    public HashSet<string> SetEnabled(PluginResolution resolution, IEnumerable<string> disabledByDefault,
        IEnumerable<string> ids, bool enabled)
    {
        var choices = ReadChoices();
        foreach (var id in ids) choices[id] = enabled;
        var disabled = Blocked(resolution, ApplyChoices(disabledByDefault, choices));
        // Keep old clients' disabled snapshot readable; only explicit choices override future defaults.
        Write(disabled);
        WriteFile(ChoicesPath, choices.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        return disabled;
    }
    public HashSet<string> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
        if (ids == null || ids.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid disabled plugin preferences.");
        return new(ids, StringComparer.OrdinalIgnoreCase);
    }
    public void Write(IEnumerable<string> disabled)
    {
        WriteFile(_path, disabled.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static HashSet<string> RequiredClosure(PluginResolution resolution, string id)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string key)
        {
            if (!result.Add(key)) return;
            foreach (var dependency in resolution.LoadOrder.Single(m => m.Id.Equals(key, StringComparison.OrdinalIgnoreCase)).Dependencies.Where(d => !d.Optional)) Visit(dependency.Id);
        }
        Visit(id);
        return result;
    }
    public static HashSet<string> Blocked(PluginResolution resolution, IEnumerable<string> disabled)
    {
        var result = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in resolution.LoadOrder)
            if (manifest.Dependencies.Any(d => !d.Optional && result.Contains(d.Id))) result.Add(manifest.Id);
        return result;
    }
}
