using Avalonia;
using Avalonia.Controls;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Hides the native browser surface without detaching the preloaded page.</summary>
public sealed class NativeWebViewPageVisibility : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<NativeWebViewPageVisibility, Control, bool>(
            "IsActive", defaultValue: true, inherits: true);

    public static readonly AttachedProperty<bool> ContentReadyProperty =
        AvaloniaProperty.RegisterAttached<NativeWebViewPageVisibility, NativeWebView, bool>(
            "ContentReady", defaultValue: false);

    static NativeWebViewPageVisibility()
    {
        IsActiveProperty.Changed.AddClassHandler<NativeWebView>((view, _) => UpdateVisibility(view));
        ContentReadyProperty.Changed.AddClassHandler<NativeWebView>((view, _) => UpdateVisibility(view));
    }

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);
    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);
    public static bool GetContentReady(NativeWebView view) => view.GetValue(ContentReadyProperty);
    public static void SetContentReady(NativeWebView view, bool value) => view.SetValue(ContentReadyProperty, value);

    private static void UpdateVisibility(NativeWebView view) =>
        view.IsVisible = GetIsActive(view) && GetContentReady(view);
}
