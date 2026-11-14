namespace Heron.Pregel.Automation;

/// <summary>Stable automation template identity; separate from UI display metadata and resource locks.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TemplateIdAttribute : Attribute
{
    public TemplateIdAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }
}
