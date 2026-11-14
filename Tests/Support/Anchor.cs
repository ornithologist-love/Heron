/// <summary>
///     The written fact a test protects: a document path relative to the Heron root, optionally with
///     <c>#L-0xx</c> for a lesson. Red means a bug, or the fact changed and is rewritten in the same commit.
///     Rules: <c>AGENTS.md</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class AnchorAttribute(string target) : Attribute
{
    public string Target { get; } = target;
}
