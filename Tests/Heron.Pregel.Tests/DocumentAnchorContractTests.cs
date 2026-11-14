namespace Heron.Pregel.Tests;

[Anchor("Docs/pregel.md#构建与验证边界")]
public sealed class DocumentAnchorContractTests
{
    [Fact]
    public void AnAnchorBelongsToItsTestClassOrMethod()
    {
        var source = """
            [Anchor("Docs/mock.md")]
            class First { [Fact] void Covered() {} }
            class Second {
                [Fact] void Missing() {}
                [Theory, Anchor("Docs/mock.md")] void Covered() {}
            }
            // [Fact] in prose is not a test.
            """;
        DocumentAnchorContract.MissingCSharpAnchors(source).Should().ContainSingle().Which.Should().StartWith("Missing:");
    }

    [Theory]
    [InlineData("# Valid title", "valid-title", true)]
    [InlineData("## Valid title", "Valid title", true)]
    [InlineData("Valid title\n---", "valid-title", true)]
    [InlineData("Only body text mentions old-title", "old-title", false)]
    [InlineData("```md\n# Hidden title\n```", "hidden-title", false)]
    [InlineData("<a id=\"custom\"></a>", "custom", true)]
    [InlineData("`<a id=\"custom\"></a>`", "custom", false)]
    [InlineData("<!--\n# Hidden title\n-->", "hidden-title", false)]
    [InlineData("See L-007 for the rule", "L-007", true)]
    [InlineData("See L-0070", "L-007", false)]
    public void FragmentsNameDocumentTargets(string document, string fragment, bool expected) =>
        DocumentAnchorContract.HasFragment(document, fragment).Should().Be(expected);
}
