using global::Avalonia.Controls;
using Heron.Pregel.Automation;

namespace Heron.Pregel.Avalonia;

/// <summary>Product panels supplied by the embedding host. Omitted contributions have no UI entry.</summary>
public sealed class AutomationPageExtensions
{
    public Func<AutomationPagePanel>? CreateSettingsPanel { get; init; }
    public Func<AutomationHost, AutomationTask, string, AutomationPagePanel>? CreateWorkflowUploadPanel { get; init; }
}

/// <summary>A host-owned panel shown inside the workspace's modal surface.</summary>
public sealed class AutomationPagePanel(Control content, Func<Task>? initialize = null, Control? initialFocus = null)
{
    public Control Content { get; } = content;
    public Control? InitialFocus { get; } = initialFocus;
    public event Action<bool>? CloseRequested;
    public Task InitializeAsync() => initialize?.Invoke() ?? Task.CompletedTask;
    public void Close(bool saved = false) => CloseRequested?.Invoke(saved);
}
