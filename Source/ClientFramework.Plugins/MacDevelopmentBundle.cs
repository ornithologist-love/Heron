using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Heron.ClientFramework.Plugins;

public sealed record MacDevelopmentBundleOptions(
    string BundleIdentifierPrefix,
    string BundleName,
    string DisplayName,
    string ExecutableName,
    string IconFileName,
    string? AssetsDirectory = null,
    Func<int, string>? IconPngFileName = null,
    string IconSetFolderName = "AppIcon.iconset");

/// <summary>Creates a macOS <c>.app</c> development wrapper around an assembled host output directory.</summary>
public static class MacDevelopmentBundle
{
    private static readonly Encoding PlistEncoding = new UTF8Encoding(false);

    public static void Ensure(string outputDirectory, MacDevelopmentBundleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsMacOS()) return;

        var output = Path.GetFullPath(outputDirectory);
        var bundle = output + ".app";
        var contents = Path.Combine(bundle, "Contents");
        var resources = Path.Combine(contents, "Resources");
        Directory.CreateDirectory(resources);

        var executableDirectory = Path.Combine(contents, "MacOS");
        if (!Directory.Exists(executableDirectory))
            Directory.CreateSymbolicLink(executableDirectory, output);
        else if (new DirectoryInfo(executableDirectory).ResolveLinkTarget(true)?.FullName != output)
            throw new InvalidDataException("Mac development bundle must link to the current host output.");

        if (!string.IsNullOrWhiteSpace(options.AssetsDirectory) && options.IconPngFileName != null)
        {
            var iconSet = Path.Combine(resources, options.IconSetFolderName);
            Directory.CreateDirectory(iconSet);
            foreach (var (name, size) in new (string, int)[]
            {
                ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
                ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
                ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256), ("icon_256x256.png", 256),
            })
                File.Copy(Path.Combine(options.AssetsDirectory, options.IconPngFileName(size)), Path.Combine(iconSet, name), overwrite: true);

            var icon = Path.Combine(resources, options.IconFileName);
            using var iconProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/iconutil",
                ArgumentList = { "-c", "icns", iconSet, "-o", icon },
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start macOS iconutil.");
            iconProcess.WaitForExit();
            if (iconProcess.ExitCode != 0) throw new InvalidOperationException("Mac development icon creation failed.");
        }

        File.WriteAllText(Path.Combine(contents, "Info.plist"), BuildInfoPlist(output, options), PlistEncoding);
        Console.WriteLine($"Mac development bundle ready: {bundle}");
    }

    public static string BuildInfoPlist(string outputDirectory, MacDevelopmentBundleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(outputDirectory))).ToLowerInvariant()[..16];
        var plist = new XDocument(
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "CFBundleIdentifier"), new XElement("string", options.BundleIdentifierPrefix + identity),
                    new XElement("key", "CFBundleName"), new XElement("string", options.BundleName),
                    new XElement("key", "CFBundleDisplayName"), new XElement("string", options.DisplayName),
                    new XElement("key", "CFBundleExecutable"), new XElement("string", options.ExecutableName),
                    new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL"),
                    new XElement("key", "CFBundleIconFile"), new XElement("string", options.IconFileName),
                    new XElement("key", "NSHighResolutionCapable"), new XElement("true"))));
        using var writer = new PlistWriter();
        plist.Save(writer);
        return writer.ToString();
    }

    private sealed class PlistWriter : StringWriter
    {
        public override Encoding Encoding => PlistEncoding;
    }
}
