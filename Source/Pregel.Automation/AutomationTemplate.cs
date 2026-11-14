using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Projects UI metadata and binds a fresh input for every preview, validation and execution.</summary>
public abstract class AutomationTemplate<TInput> : IAutomationTemplate, IAutomationInputSchemaConsumer where TInput : class, new()
{
    private AutomationInputSchema _inputSchema = AutomationInputSchema.Get(typeof(TInput));

    void IAutomationInputSchemaConsumer.ConfigureInput(PregelInputSchema definition) =>
        _inputSchema = _inputSchema.WithDefinition(definition);

    protected TInput BindInput(AutomationTask task) => (TInput)_inputSchema.Read(task);

    /// <summary>Projects a typed C# visibility predicate without evaluating it while constructing the template.</summary>
    protected Func<AutomationTask, bool> If(Func<TInput, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return task => condition(BindInput(task));
    }

    public string Id => AutomationTemplateIdentity.Read(GetType());
    public string Title => AutomationTemplateMetadata.Title(GetType());
    public string Description => AutomationTemplateMetadata.Description(GetType());
    public string Icon => AutomationTemplateMetadata.Icon(GetType());
    public virtual bool ShowInCatalog => true;
    public virtual IReadOnlyList<AutomationField> Fields => _inputSchema.Fields;
    public virtual string? RenderParameters(AutomationTask task) => null;

    public virtual Task<IReadOnlyList<AutomationChoice>?> ListFieldChoicesAsync(AutomationTask task, string fieldKey, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<AutomationChoice>?>(null);

    public string? Validate(AutomationTask task)
    {
        try { return Validate(task, BindInput(task)); }
        catch (InvalidDataException error) { return error.Message; }
    }

    public virtual IEnumerable<AutomationRuleViolation> CheckRules(AutomationTask task, IReadOnlyList<AutomationTask> siblings) => [];
    public virtual IReadOnlyList<string> ResourceKeys(AutomationTask task) => [];
    public virtual IReadOnlyList<AutomationTask> ImportLegacyTasks() => [];
    public virtual void OnTaskDeleted(AutomationTask task) { }
    public IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task) => PlanSteps(task, BindInput(task));
    public Task<AutomationRunResult> RunAsync(AutomationRunContext context, CancellationToken token) =>
        RunAsync(context, BindInput(context.Task), token);

    protected virtual string? Validate(AutomationTask task, TInput input) => null;
    protected abstract IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, TInput input);
    protected abstract Task<AutomationRunResult> RunAsync(AutomationRunContext context, TInput input, CancellationToken token);
}

/// <summary>Review continuations bind from the saved run's task snapshot.</summary>
public abstract class AutomationReviewTemplate<TInput> : AutomationTemplate<TInput>, IAutomationReviewTemplate
    where TInput : class, new()
{
    public virtual bool SupportsChatTransfer => false;
    public virtual Task<AutomationChatSession?> GetChatSessionAsync(AutomationReview review, CancellationToken token) =>
        Task.FromResult(review.ChatSession);

    public Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review,
        AutomationReviewDecision decision, CancellationToken token) =>
        ReviewAsync(context, BindInput(context.Task), review, decision, token);

    protected abstract Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, TInput input,
        AutomationReview review, AutomationReviewDecision decision, CancellationToken token);
}
