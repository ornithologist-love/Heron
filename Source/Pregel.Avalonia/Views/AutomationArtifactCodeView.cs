using System;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Markup.Xaml.MarkupExtensions;
using global::Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using EditorTextArea = AvaloniaEdit.Editing.TextArea;

namespace Heron.Pregel.Avalonia.Views;

/// <summary>Native read-only artifact source preview; no browser or additional grammar runtime.</summary>
public sealed class AutomationArtifactCodeView : TextEditor
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<AutomationArtifactCodeView, string?>(nameof(SourceText));
    public static readonly StyledProperty<string?> FileExtensionProperty =
        AvaloniaProperty.Register<AutomationArtifactCodeView, string?>(nameof(FileExtension));
    private bool _initialized;

    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public string? FileExtension { get => GetValue(FileExtensionProperty); set => SetValue(FileExtensionProperty, value); }
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public AutomationArtifactCodeView()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        WordWrap = true;
        FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace");
        FontSize = 14;
        FontWeight = FontWeight.Normal;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        this.Bind(BackgroundProperty, new DynamicResourceExtension("BgSurfaceBrush"));
        this.Bind(ForegroundProperty, new DynamicResourceExtension("TextDefaultBrush"));
        TextArea.Bind(EditorTextArea.SelectionBrushProperty, new DynamicResourceExtension("NavSelectedBgBrush"));
        TextArea.Bind(EditorTextArea.SelectionForegroundProperty, new DynamicResourceExtension("TextDefaultBrush"));
        // Runs with Chinese glyphs must select a Chinese font directly instead of
        // falling back from Mono through the application's incomplete subset font.
        TextArea.TextView.LineTransformers.Add(new ChineseFontTransformer());
        _initialized = true;
        ActualThemeVariantChanged += (_, _) => TextArea.TextView.Redraw();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // The package installs its search panel with the editor. Supply the host's
        // resources directly rather than importing a second application's theme.
        SearchPanel.Bind(BackgroundProperty, new DynamicResourceExtension("BgSurfaceBrush"));
        SearchPanel.Bind(ForegroundProperty, new DynamicResourceExtension("TextDefaultBrush"));
        SearchPanel.Bind(BorderBrushProperty, new DynamicResourceExtension("BorderDefaultBrush"));
        SearchPanel.FontFamily = new FontFamily("Microsoft YaHei UI,Microsoft YaHei,Segoe UI");
        SearchPanel.FontSize = FontSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_initialized) return;
        if (change.Property == SourceTextProperty)
        {
            Text = SourceText ?? "";
            ScrollToHome();
        }
        else if (change.Property == FileExtensionProperty)
        {
            var extension = FileExtension?.ToLowerInvariant();
            SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(extension switch
            {
                ".jsonl" => ".json",
                ".csx" => ".cs",
                _ => extension ?? ""
            });
        }
    }

    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition definition) =>
        new ArtifactColorizer(this, definition);

    // Keep built-in grammar parsing, but resolve colors against the host's Light/Dark
    // resources without mutating the process-wide highlighting definitions.
    private sealed class ArtifactColorizer(AutomationArtifactCodeView owner, IHighlightingDefinition definition)
        : HighlightingColorizer(definition)
    {
        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            base.ApplyColorToElement(element, color);
            var name = color.Name ?? "";
            var key = name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ? "TextSecondaryBrush"
                : name is "String" or "AttributeValue" or "CData" ? "TextPrimaryBrush"
                : name is "Punctuation" ? "TextDefaultBrush" : "TextLinkBrush";
            if (owner.TryFindResource(key, out var resource) && resource is IBrush brush)
                element.TextRunProperties.SetForegroundBrush(brush);
        }
    }

    private sealed class ChineseFontTransformer : DocumentColorizingTransformer
    {
        private static readonly Typeface ChineseTypeface = new(
            new FontFamily("Microsoft YaHei UI,Microsoft YaHei,Segoe UI"), FontStyle.Normal, FontWeight.Normal);

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            var position = 0;
            while (position < text.Length)
            {
                if (char.IsAscii(text[position])) { position++; continue; }
                var start = position++;
                while (position < text.Length && !char.IsAscii(text[position])) position++;
                ChangeLinePart(line.Offset + start, line.Offset + position,
                    element => element.TextRunProperties.SetTypeface(ChineseTypeface));
            }
        }
    }
}
