using Heron.Pregel.Automation;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Heron.AutomationTenants;

/// <summary>Installed code is publisher-owned; mutable configuration lives beside the install root.</summary>
public static class AutomationPublishedSource
{
    internal static string Root => RemoteAutomationEnvironment.Current.ScriptsRoot();
    internal static string StateRoot => Path.Combine(Path.GetDirectoryName(Root)!, "Pregel-state");
    internal static string CatalogPath => Path.Combine(StateRoot, "catalog.json");
    internal static string ManifestPath(string? sourceId) => sourceId == null ? CatalogPath
        : Path.Combine(StateRoot, "sources", sourceId.ToUpperInvariant() + ".json");

    internal static bool Contains(string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string InputPath(string path)
    {
        if (!Contains(path)) return path;
        var target = Path.Combine(StateRoot, "inputs", Path.GetRelativePath(Root, path));
        if (File.Exists(target) || !File.Exists(path)) return File.Exists(target) ? target : path;
        // Preserve the existing shared-file trigger semantics, without ever editing installed code.
        var source = JsonNode.Parse(AutomationStore.ReadShared(path)) as JsonObject;
        if (source == null || !AutomationScriptInput.HasTriggers(source)) return path;
        var triggers = source["triggers"]!;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, new JsonObject { ["triggers"] = triggers.DeepClone() }.ToJsonString());
            File.Move(temporary, target);
        }
        catch (IOException) when (File.Exists(target)) { }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
    }

    internal static void PreserveInputs(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in AutomationLocalSource.ContentFiles(directory)
                     .Where(file => Path.GetFileName(file).Equals("Input.json", StringComparison.OrdinalIgnoreCase)))
        {
            try { InputPath(file); }
            catch (System.Text.Json.JsonException)
            {
                // A corrupt install is replaced, not promoted into a permanent configuration override.
            }
        }
    }

    /// <summary>
    ///     True when a path carries the current protection: a directory denies changes (Windows access rule,
    ///     elsewhere no write permission); a file carries no mark of its own, so a copy of it stays writable.
    ///     Content verification, not these marks, decides whether installed code may run.
    /// </summary>
    internal static bool IsReadOnly(string path)
    {
        if (Directory.Exists(path))
            return OperatingSystem.IsWindows() ? HasWindowsRule(path)
                : (File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
        return OperatingSystem.IsWindows() ? (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0
            : (File.GetUnixFileMode(path) & UnixFileMode.UserWrite) != 0;
    }

    internal static bool IsReadOnlyTree(string directory) => Directory.Exists(directory)
        && IsReadOnly(directory) && Directory.EnumerateFileSystemEntries(directory).All(path =>
            Directory.Exists(path) ? IsReadOnlyTree(path) : IsReadOnly(path));

    internal static void MakeWritable(string directory)
    {
        if (!Directory.Exists(directory)) return;
        SetDirectory(directory, false);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(path);
            if (Directory.Exists(path)) MakeWritable(path);
            else ClearFileMark(path);
        }
    }

    internal static void Protect(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(path);
            if (Directory.Exists(path)) Protect(path);
            else ClearFileMark(path);
        }
        SetDirectory(directory, true);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("发布安装目录不能包含链接：" + path);
    }

    private static void SetDirectory(string path, bool readOnly)
    {
        RejectLink(path);
        if (OperatingSystem.IsWindows()) SetWindowsRule(path, readOnly);
        else
        {
            var mode = File.GetUnixFileMode(path);
            var write = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            File.SetUnixFileMode(path, readOnly ? mode & ~write : mode | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    ///     Files are never marked read-only: the attribute and Unix modes travel with every copy, which made files
    ///     scripts copy out of a package (configuration templates, assets) unwritable. Protection on Windows comes
    ///     from the inherited directory rule, which a copy does not take along. Clearing the mark also migrates
    ///     installations protected by earlier versions.
    /// </summary>
    private static void ClearFileMark(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        else File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule WindowsRule() => new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);

    [SupportedOSPlatform("windows")]
    private static bool HasWindowsRule(string path) => new DirectoryInfo(path).GetAccessControl()
        .GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
        .Any(rule => rule.IdentityReference == WindowsRule().IdentityReference
            && rule.AccessControlType == AccessControlType.Deny && rule.FileSystemRights == WindowsRule().FileSystemRights
            && rule.InheritanceFlags == WindowsRule().InheritanceFlags);

    [SupportedOSPlatform("windows")]
    private static void SetWindowsRule(string path, bool readOnly)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.RemoveAccessRuleSpecific(WindowsRule());
        if (readOnly) security.AddAccessRule(WindowsRule());
        directory.SetAccessControl(security);
    }
}
