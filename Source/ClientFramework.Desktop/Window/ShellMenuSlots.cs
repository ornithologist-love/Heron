namespace Heron.ClientFramework.Shell;

/// <summary>
///     Order of the user menu entries. Host entries keep the order the host declared them in. The shell's
///     theme entry goes first unless the host placed it itself; hidden entries stay in place so a binding that
///     shows them later does not move them.
/// </summary>
public static class ShellMenuSlots
{
    public static IReadOnlyList<T> Compose<T>(IEnumerable<T> hostEntries, Func<T, bool> isThemeEntry, Func<T> createThemeEntry)
    {
        var entries = hostEntries.ToList();
        if (!entries.Any(isThemeEntry)) entries.Insert(0, createThemeEntry());
        return entries;
    }
}
