using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>One input definition supplies the form, defaults and persisted parameter binding.</summary>
public static class AutomationInput<TInput> where TInput : class, new()
{
    private static readonly AutomationInputSchema Schema = AutomationInputSchema.Get(typeof(TInput));
    public static IReadOnlyList<AutomationField> Fields => Schema.Fields;
    public static TInput Read(AutomationTask task) => (TInput)Schema.Read(task);
    public static IReadOnlyDictionary<string, string> Write(TInput input) => Schema.Write(input);
}

/// <summary>An empty input for templates with no editable parameters.</summary>
public sealed class AutomationEmptyInput { }

internal interface IAutomationInputSchemaConsumer
{
    void ConfigureInput(PregelInputSchema definition);
}

internal sealed class AutomationInputSchema
{
    // The schema refers back to its type; this association must not keep collectible scripts alive.
    private static readonly ConditionalWeakTable<Type, AutomationInputSchema> Schemas = new();
    private readonly Type _type;
    private readonly (PropertyInfo Property, AutomationField Field)[] _bindings;
    private readonly JsonElement? _defaults;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static AutomationInputSchema Get(Type type) => Schemas.GetValue(type, static input => new(input));

    public static AutomationInputSchema? Annotated(Type type) => type.IsClass &&
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.IsDefined(typeof(DisplayNameAttribute), inherit: true)) ? Get(type) : null;

    private AutomationInputSchema(Type type)
    {
        _type = type;
        if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"自动化输入 {type.Name} 必须有无参构造函数。");
        var defaults = Create();
        var bindings = new List<(PropertyInfo, AutomationField)>();
        // Derived input properties precede shared base input properties (for example the result webhook).
        for (var owner = type; owner is not null && owner != typeof(object); owner = owner.BaseType)
        foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .OrderBy(property => property.MetadataToken))
        {
            var name = property.GetCustomAttribute<DisplayNameAttribute>(inherit: true);
            if (name is null) continue;
            var readOnly = property.GetCustomAttribute<ReadOnlyAttribute>(inherit: true)?.IsReadOnly == true;
            if (property.GetIndexParameters().Length != 0 || property.GetMethod?.IsPublic != true
                || (!readOnly && property.SetMethod?.IsPublic != true))
                throw new InvalidOperationException($"自动化输入 {type.Name}.{property.Name} 必须可公开读写。");
            var valueType = property.PropertyType;
            if (valueType != typeof(string) && valueType != typeof(string[]) && !valueType.IsPrimitive && !valueType.IsEnum && valueType != typeof(decimal))
                throw new InvalidOperationException($"自动化输入 {type.Name}.{property.Name} 的类型暂不支持表单绑定。");
            var display = property.GetCustomAttribute<DisplayFieldAttribute>(inherit: true);
            if (property.GetCustomAttribute<DisplayParameterTypeAttribute>(inherit: true) is { } parameter)
                ValidateParameterType(property, parameter.Type);
            var kind = display?.Kind ?? (valueType == typeof(bool) ? DisplayFieldKind.Toggle
                : valueType == typeof(string[]) ? DisplayFieldKind.MultiChoice
                : valueType.IsEnum ? DisplayFieldKind.Choice : DisplayFieldKind.Text);
            if (kind == DisplayFieldKind.Toggle && valueType != typeof(bool)
                || (kind == DisplayFieldKind.MultiChoice) != (valueType == typeof(string[]))
                || (kind is DisplayFieldKind.Folder or DisplayFieldKind.Multiline) && valueType != typeof(string))
                throw new InvalidOperationException($"自动化输入 {type.Name}.{property.Name} 的控件与类型不匹配。");
            var key = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            var choices = display?.Choices ?? (valueType.IsEnum ? Array.AsReadOnly(Enum.GetNames(valueType)) : null);
            bindings.Add((property, new(key, name.DisplayName, kind, Format(property.GetValue(defaults)),
                property.GetCustomAttribute<DisplayNameHintAttribute>(inherit: true)?.Hint, choices)
                { IsReadOnly = readOnly, GlobalValue = GlobalValue(key) }));
        }
        _bindings = bindings.ToArray();
        if (_bindings.Select(binding => binding.Field.Key).Distinct(StringComparer.Ordinal).Count() != _bindings.Length)
            throw new InvalidOperationException($"自动化输入 {type.Name} 存在重复参数键。");
        Fields = Array.AsReadOnly(_bindings.Select(binding => binding.Field).ToArray());
    }

    private AutomationInputSchema(Type type, (PropertyInfo Property, AutomationField Field)[] bindings, JsonElement defaults)
    {
        _type = type;
        _bindings = bindings;
        _defaults = defaults;
        Fields = Array.AsReadOnly(bindings.Select(binding => binding.Field).ToArray());
    }

    internal AutomationInputSchema WithDefinition(PregelInputSchema definition)
    {
        if (definition.Values.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("自动化输入 Schema 必须是 JSON 对象。");
        var defaults = definition.Values.Deserialize(_type, Json)
            ?? throw new InvalidDataException("自动化输入默认值不能为空。");
        foreach (var name in definition.With.Keys)
            if (!_bindings.Any(binding => binding.Field.Key == name))
                throw new InvalidDataException($"输入参数「{name}」未在 {_type.Name} 中声明展示标注。");
        var bindings = _bindings.Select(binding =>
        {
            var field = binding.Field with { DefaultValue = Format(binding.Property.GetValue(defaults)) };
            if (definition.With.TryGetValue(field.Key, out var parameter) && parameter.With is { } choices)
            {
                if (field.Kind is DisplayFieldKind.Folder or DisplayFieldKind.Multiline)
                    throw new InvalidDataException($"「{field.Label}」的展示方式不能同时配置 value 候选项。");
                var options = choices.Select(choice => new AutomationChoice(PregelInputSchema.ScalarText(choice.Value), choice.Label)
                    { ToolTip = PregelInputSchema.ScalarText(choice.Value) }).ToArray();
                if (field.Kind == DisplayFieldKind.MultiChoice)
                {
                    if (choices.Any(choice => choice.Value.ValueKind != JsonValueKind.String))
                        throw new InvalidDataException($"「{field.Label}」的多选候选项必须是字符串。");
                }
                else foreach (var option in options) ConvertValue(binding.Property, option.Value);
                field = field with { Kind = field.Kind == DisplayFieldKind.MultiChoice ? field.Kind : DisplayFieldKind.Choice,
                    Choices = null, With = Array.AsReadOnly(options) };
            }
            return (binding.Property, field);
        }).ToArray();
        return new(_type, bindings, definition.Values);
    }

    public IReadOnlyList<AutomationField> Fields { get; }

    /// <summary>
    ///     Machine-local value of a 「全局设置」parameter, or null when the key is a normal task parameter.
    ///     Recomputed on every projection so a saved 全局设置 change is used by the next preview and run.
    /// </summary>
    public static string? GlobalValue(string key) => AutomationRuntime.GlobalParameter?.Invoke(key);

    public object Read(AutomationTask task)
    {
        var input = Create();
        foreach (var (property, field) in _bindings)
        {
            if (field.IsReadOnly) continue;
            // 优先级：任务参数 > 全局设置 > 输入默认值；全局设置留空时回落到默认值。
            string? text = task.Parameters.TryGetValue(field.Key, out var saved)
                && (task.ParametersResolved || !string.IsNullOrWhiteSpace(saved)) ? saved : null;
            if (text == null && !task.ParametersResolved)
            {
                var fallback = GlobalValue(field.Key);
                if (string.IsNullOrWhiteSpace(fallback)) continue;
                text = fallback;
            }
            if (text == null) continue;
            try
            {
                property.SetValue(input, ConvertValue(property, text));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException or OverflowException or JsonException)
            {
                throw new InvalidDataException($"「{field.Label}」的参数值无效。", error);
            }
        }
        return input;
    }

    public IReadOnlyDictionary<string, string> Write(object input) => _bindings.Where(binding => !binding.Field.IsReadOnly).ToDictionary(
        binding => binding.Field.Key, binding => Format(binding.Property.GetValue(input)), StringComparer.Ordinal);

    private object Create() => _defaults is { } defaults ? defaults.Deserialize(_type, Json)! : Activator.CreateInstance(_type)!;

    private static object ConvertValue(PropertyInfo property, string text)
    {
        if (property.PropertyType == typeof(string[]))
            return JsonSerializer.Deserialize<string[]>(text, Json) ?? throw new FormatException();
        var value = property.PropertyType == typeof(string) ? text
            : TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(text);
        if (value is null || property.PropertyType.IsEnum && !Enum.IsDefined(property.PropertyType, value))
            throw new FormatException();
        return value;
    }

    private static void ValidateParameterType(PropertyInfo property, ParameterType type)
    {
        var code = Type.GetTypeCode(property.PropertyType);
        var integer = code is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;
        var valid = type switch
        {
            ParameterType.String => property.PropertyType == typeof(string) || property.PropertyType.IsEnum,
            ParameterType.Boolean => property.PropertyType == typeof(bool),
            ParameterType.Integer => integer && !property.PropertyType.IsEnum,
            ParameterType.Number => (integer || code is TypeCode.Single or TypeCode.Double or TypeCode.Decimal)
                && !property.PropertyType.IsEnum,
            _ => false
        };
        if (!valid) throw new InvalidOperationException($"{property.DeclaringType?.Name}.{property.Name} 的 DisplayParameterType 与属性类型不匹配。");
    }
    private static string Format(object? value) => value is string[] values ? JsonSerializer.Serialize(values, Json)
        : value is bool flag ? flag ? "true" : "false"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
