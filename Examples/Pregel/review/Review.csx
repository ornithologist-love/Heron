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
    protected override PregelGraph<ReviewInput, ReviewInput> CreateGraph() => new ReviewGraph().Build();

    protected override string SuccessSummary(ReviewInput input) => "Applied: " + input.Note;
}

sealed class ReviewGraph
{
    [DisplayName("Prepare")]
    void Prepare(ReviewInput input, PregelNodeContext context, CancellationToken token) =>
        context.Log("Prepared: " + input.Note);

    [DisplayName("Approve")]
    void Approve(ReviewInput input, PregelNodeContext context, CancellationToken token)
    {
        if (!context.HasResumeValue)
        {
            context.Interrupt("Apply this change?", input.Note);
            return;
        }
        context.Log("Approved with " + context.ResumeValue<string>());
    }

    [DisplayName("Apply")]
    void Apply(ReviewInput input, PregelNodeContext context, CancellationToken token) => context.Log("Applied");

    public PregelGraph<ReviewInput, ReviewInput> Build() =>
        Pregel.Define<ReviewInput>("ReviewExample")
            .Stage("main", "Main", stage => stage
                .Then(Prepare)
                .Then(Approve)
                .Then(Apply))
            .Compile();
}
