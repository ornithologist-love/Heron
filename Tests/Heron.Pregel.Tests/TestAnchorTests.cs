using System.Text.RegularExpressions;

namespace Heron.Pregel.Tests;

/// <summary>
///     Document alignment baseline: every framework test, C# or Go, names the written fact it protects,
///     and that fact still exists. An unanchored test cannot tell a bug from a normal change.
/// </summary>
[Anchor("Docs/pregel.md#构建与验证边界")]
public sealed partial class TestAnchorTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void EveryCSharpTestIsAnchoredToAnExistingDocument()
    {
        var problems = new List<string>();
        foreach (var file in Files(Path.Combine(Root, "Tests"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(Root, file);
            foreach (var missing in DocumentAnchorContract.MissingCSharpAnchors(source))
                problems.Add($"{relative}:{missing} test has no [Anchor]");
            foreach (var (target, line) in DocumentAnchorContract.CSharpAnchors(source))
                Check(target, $"{relative}:{line}", problems);
        }

        problems.Should().BeEmpty("every test must point at the documented fact it protects");
    }

    [Fact]
    public void EveryGoTestIsAnchoredToAnExistingDocument()
    {
        var problems = new List<string>();
        var files = Files(Path.Combine(Root, "Source"), "*_test.go").ToList();
        files.Should().NotBeEmpty("the tenant control service keeps its contract tests beside the code");
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            var relative = Path.GetRelativePath(Root, file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!GoTest().IsMatch(lines[index])) continue;
                var anchors = new List<string>();
                for (var above = index - 1; above >= 0 && lines[above].TrimStart().StartsWith("//", StringComparison.Ordinal); above--)
                    if (GoAnchor().Match(lines[above]) is { Success: true } match) anchors.Add(match.Groups[1].Value.Trim());
                if (anchors.Count == 0) problems.Add($"{relative}:{index + 1} test has no // Anchor: comment");
                foreach (var anchor in anchors) Check(anchor, $"{relative}:{index + 1}", problems);
            }
        }

        problems.Should().BeEmpty("every test must point at the documented fact it protects");
    }

    private static void Check(string target, string location, List<string> problems)
    {
        var (document, fragment) = target.Split('#', 2) is [var d, var f] ? (d, f) : (target, null);
        var path = Path.GetFullPath(Path.Combine(Root, document));
        if (!File.Exists(path))
            problems.Add($"{location} anchor document missing: {document}");
        else if (fragment != null && !DocumentAnchorContract.HasFragment(File.ReadAllText(path), fragment))
            problems.Add($"{location} anchor '{fragment}' not found in {document}");
    }

    private static IEnumerable<string> Files(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "Support" or "PluginFixtures"));

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Heron.sln"))) return directory.FullName;
        throw new InvalidOperationException("Heron.sln not found above the test output directory.");
    }

    [GeneratedRegex(@"^func Test[A-Z_]\w*\(")]
    private static partial Regex GoTest();

    [GeneratedRegex(@"^\s*//\s*Anchor:\s*(.+?)\s*$")]
    private static partial Regex GoAnchor();
}
