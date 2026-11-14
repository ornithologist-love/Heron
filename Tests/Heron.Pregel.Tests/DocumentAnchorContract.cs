using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Heron.Pregel.Tests;

internal static class DocumentAnchorContract
{
    internal static IEnumerable<(string Target, int Line)> CSharpAnchors(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<AttributeSyntax>()
            .Where(attribute => Name(attribute) == "Anchor")
            .Select(attribute => (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText
                    : throw new InvalidDataException("Anchor must name a document with a string literal."),
                attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

    internal static IReadOnlyList<string> MissingCSharpAnchors(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => Name(attribute) is "Fact" or "Theory"))
            .Where(method => !HasAnchor(method.AttributeLists)
                && !HasAnchor(method.Ancestors().OfType<TypeDeclarationSyntax>().First().AttributeLists))
            .Select(method => $"{method.Identifier.ValueText}:{method.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToArray();
    }

    internal static bool HasFragment(string document, string fragment)
    {
        var text = Regex.Replace(Prose(document), @"<!--[\s\S]*?-->", "");
        if (Regex.IsMatch(Regex.Replace(text, @"`+[^`\n]*`+", ""), $"<(?:[a-z][^>]*)\\s(?:id|name)\\s*=\\s*['\"]{Regex.Escape(fragment)}['\"]", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(fragment, "^L-[0-9]{3}$")
            && Regex.IsMatch(text, $@"(?<![\w-]){Regex.Escape(fragment)}(?![\w-])")) return true;
        var counts = new Dictionary<string, int>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var heading = Regex.Match(lines[i], @"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$");
            var title = heading.Success ? heading.Groups[1].Value
                : i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^ {0,3}(?:=+|-+)\s*$") ? lines[i].Trim() : null;
            if (string.IsNullOrEmpty(title)) continue;
            title = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(title, @"\[([^\]]+)\]\([^)]*\)", "$1"), @"<[^>]*>|[`*~]", ""));
            var slug = string.Concat(title.ToLowerInvariant().Select(c => char.IsWhiteSpace(c) ? '-' : c)
                .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
            counts.TryGetValue(slug, out var count);
            counts[slug] = count + 1;
            if (title == fragment || (count == 0 ? slug : $"{slug}-{count}") == fragment) return true;
        }
        return false;
    }

    private static string Prose(string document)
    {
        var lines = document.Replace("\r\n", "\n").Split('\n');
        var metadata = lines.FirstOrDefault() == "---";
        char fence = '\0'; var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (metadata) { lines[i] = ""; if (i > 0 && line == "---") metadata = false; continue; }
            var marker = Regex.Match(line, @"^ {0,3}(`{3,}|~{3,})");
            if (fence != '\0')
            {
                lines[i] = "";
                if (marker.Success && marker.Value.TrimStart()[0] == fence && marker.Groups[1].Length >= fenceLength
                    && line[marker.Length..].Trim().Length == 0) fence = '\0';
            }
            else if (marker.Success)
            {
                fence = marker.Groups[1].Value[0]; fenceLength = marker.Groups[1].Length; lines[i] = "";
            }
        }
        return string.Join('\n', lines);
    }

    private static bool HasAnchor(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes).Any(attribute => Name(attribute) == "Anchor");

    private static string Name(AttributeSyntax attribute) =>
        Regex.Replace(attribute.Name.ToString().Split('.').Last(), "Attribute$", "");
}
