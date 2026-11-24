namespace Heron.Core.Process;

/// <summary>Resolve fresh on every launch so installing a tool or changing PATH needs no host restart.</summary>
public static class InstalledToolLocator
{
    public static string Resolve(string tool, string vendorDirectory)
    {
        if (!OperatingSystem.IsWindows()) return tool;
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        var directories = paths.Where(path => path != null).SelectMany(path => path!.Split(';'))
            .Select(path => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
            .Where(Path.IsPathFullyQualified)
            .Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(path => !string.IsNullOrEmpty(path)).Select(path => Path.Combine(path, vendorDirectory)));
        return Find(tool + ".exe", directories, File.Exists)
            ?? throw new FileNotFoundException($"未找到 {tool}.exe。已检查 PATH 和 Program Files\\{vendorDirectory}；请确认安装了对应组件。");
    }

    internal static string? Find(string executable, IEnumerable<string> directories, Func<string, bool> exists) =>
        directories.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(exists);

    public static IReadOnlyDictionary<string, string> ReadUpdatedEnvironment(IEnumerable<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return result;
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            if (value != null) result[name] = value;
        }
        return result;
    }
}
