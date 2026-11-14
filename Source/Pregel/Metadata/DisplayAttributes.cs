namespace Heron.Pregel;

/// <summary>Display-only description; does not affect graph execution.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DisplayDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>Display-only icon name; the consuming UI resolves the icon.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class DisplayIconAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Display-only help text for an input property; does not affect execution.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DisplayNameHintAttribute(string hint) : Attribute
{
    public string Hint { get; } = hint;
}

/// <summary>Declarative input presentation; the consuming UI supplies controls.</summary>
public enum DisplayFieldKind { Toggle, Text, Folder, Choice, Multiline, MultiChoice }

/// <summary>The scalar type of an input parameter, independent of the editor control.</summary>
public enum ParameterType { String, Boolean, Integer, Number }

/// <summary>Declares the input parameter type. A schema parameter's value list supplies its optional choices.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DisplayParameterTypeAttribute(ParameterType type) : Attribute
{
    public ParameterType Type { get; } = type;
}

/// <summary>Display-only input presentation override; ordinary input types infer their controls.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class DisplayFieldAttribute(DisplayFieldKind kind, params string[] choices) : Attribute
{
    public DisplayFieldKind Kind { get; } = kind;
    public IReadOnlyList<string>? Choices { get; } = choices.Length == 0 ? null : Array.AsReadOnly(choices.ToArray());
}
