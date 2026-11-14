using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Heron.Pregel.Scripting;

/// <summary>Creates a syntax-only namespace migration for trusted scripts and their loaded sources.</summary>
public static class NamespaceSourceTransform
{
    public static Func<string, string> Create(IReadOnlyDictionary<string, string> namespaces)
    {
        var mappings = namespaces.OrderByDescending(pair => pair.Key.Length).ToArray();
        return source => new Names(mappings).Visit(CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(kind: SourceCodeKind.Script)).GetRoot())!.ToFullString();
    }

    private sealed class Names(KeyValuePair<string, string>[] mappings) : CSharpSyntaxRewriter
    {
        private string? Mapped(string name)
        {
            if (name.StartsWith("global::", StringComparison.Ordinal)) name = name[8..];
            foreach (var (former, current) in mappings)
                if (name == former || name.StartsWith(former + ".", StringComparison.Ordinal))
                    return "global::" + current + name[former.Length..];
            return null;
        }

        public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node) =>
            node.Name is { } name && Mapped(name.ToString()) is { } mapped
                ? node.WithName(SyntaxFactory.ParseName(mapped).WithTriviaFrom(name)) : base.VisitUsingDirective(node);

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node) =>
            Mapped(node.ToString()) is { } mapped
                ? SyntaxFactory.ParseName(mapped).WithTriviaFrom(node) : base.VisitQualifiedName(node);

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node) =>
            Mapped(node.ToString()) is { } mapped
                ? SyntaxFactory.ParseExpression(mapped).WithTriviaFrom(node) : base.VisitMemberAccessExpression(node);
    }
}
