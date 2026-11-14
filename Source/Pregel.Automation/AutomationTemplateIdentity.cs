using System.Reflection;

namespace Heron.Pregel.Automation;

/// <summary>Projects a template's own stable identity; derived templates must declare their own identity.</summary>
public static class AutomationTemplateIdentity
{
    public static string Read(Type type) =>
        type.GetCustomAttribute<TemplateIdAttribute>(inherit: false)?.Id
        ?? throw new InvalidOperationException($"自动化模板 {type.FullName} 缺少 [TemplateId] 标注。");
}
