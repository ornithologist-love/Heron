using System.Text.Json;

namespace Heron.ClientHost;

/// <summary>Per-user launcher choices: theme mode and sidebar pin. Nothing product specific.</summary>
internal sealed class HostSettings
{
    public string ThemeMode { get; set; } = "system";
    public bool SidebarPinned { get; set; } = true;

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heron", "ClientHost");
    private static string File => Path.Combine(Root, "settings.json");

    public static HostSettings Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<HostSettings>(System.IO.File.ReadAllText(File)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public static void Update(Action<HostSettings> change)
    {
        var settings = Load();
        change(settings);
        Directory.CreateDirectory(Root);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
