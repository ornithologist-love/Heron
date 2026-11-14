// A run that stops for a person, survives the process exiting, and continues from where it stopped.
return new ReviewTemplate();

sealed class ReviewInput
{
    [DisplayName("Release note")]
    public string Note { get; init; } = "Ship the new parser";
}

[DisplayName("Review example")]
[DisplayDescription("Prepares a change and waits for approval before applying it")]
[TemplateId("ReviewExample")]
sealed class ReviewTemplate : AutomationGraphTemplate<ReviewInput>
{
    protected override PregelGraph<ReviewInput> CreateGraph() => new ReviewGraph().Build();

    protected override string SuccessSummary(ReviewInput input) => "Applied: " + input.Note;
}

sealed class ReviewGraph
{
    [DisplayName("Prepare")]
    string Prepare(ReviewInput input, PregelNodeContext context, CancellationToken token)
    {
        context.Log("Prepared: " + input.Note);
        return input.Note;
    }

    [DisplayName("Approve")]
    string Approve(ReviewInput input, PregelNodeContext context, CancellationToken token)
    {
        if (!context.HasResumeValue)
        {
            context.Interrupt("Apply this change?", input.Note);
            return input.Note;
        }
        context.Log("Approved with " + context.ResumeValue<string>());
        return input.Note;
    }

    [DisplayName("Apply")]
    void Apply(ReviewInput input, PregelNodeContext context, CancellationToken token) => context.Log("Applied");

    public PregelGraph<ReviewInput> Build()
    {
        var flow = Pregel.Define<ReviewInput>("ReviewExample", "1");
        var prepared = flow.State<string>("prepared", "");
        var approved = flow.State<string>("approved", "");
        return flow
            .Stage("main", "Main", stage => stage
                .Then(Prepare, input => input, prepared)
                .Then(Approve, input => input, approved)
                .Then(Apply))
            .Compile();
    }
}
