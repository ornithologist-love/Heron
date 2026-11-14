// The smallest useful graph: typed input, two named nodes, one optional stage.
sealed class HelloInput
{
    [DisplayName("Name")]
    public string Name { get; init; } = "world";

    [DisplayName("Shout")]
    public bool Shout { get; init; }
}

static class HelloNodes
{
    [DisplayName("Greet")]
    public static void Greet(HelloInput input, PregelNodeContext context, CancellationToken token) =>
        context.Log("Hello, " + input.Name);

    [DisplayName("Shout")]
    public static void Shout(HelloInput input, PregelNodeContext context, CancellationToken token) =>
        context.Log(("Hello, " + input.Name).ToUpperInvariant());
}

return Pregel.Define<HelloInput>("Hello", "1")
    .Stage("greet", "Greet", stage => stage.Then(HelloNodes.Greet))
    .Stage("shout", "Shout", stage => stage.Then(HelloNodes.Shout),
        when: input => input.Shout, skipReason: "Shout is off")
    .Compile();
