using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Heron.ClientControls.Text;

/// <summary>
///     Compact Markdown renderer backed by one SelectableTextBlock, so the complete
///     rendered document remains selectable and copyable as a single text range.
/// </summary>
public sealed class SelectableMarkdownTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<SelectableMarkdownTextBlock, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    static SelectableMarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<SelectableMarkdownTextBlock>((control, _) => control.Render());
    }

    public SelectableMarkdownTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Render()
    {
        Inlines = RenderMarkdown(Markdown);
    }

    internal static InlineCollection RenderMarkdown(string? markdown)
    {
        var rendered = new InlineCollection();
        var document = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var firstBlock = true;
        foreach (var block in document)
        {
            if (!firstBlock) rendered.Add(new LineBreak());
            AppendBlock(rendered, block, 0);
            firstBlock = false;
        }
        return rendered;
    }

    private static void AppendBlock(InlineCollection target, Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var headingSpan = new Span
                {
                    FontWeight = FontWeight.SemiBold,
                    FontSize = heading.Level switch { 1 => 18, 2 => 16, _ => 14 }
                };
                AppendInlineContainer(headingSpan.Inlines, heading.Inline);
                target.Add(headingSpan);
                break;
            case ParagraphBlock paragraph:
                AppendInlineContainer(target, paragraph.Inline);
                break;
            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;
                var itemIndex = 0;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    if (itemIndex++ > 0) target.Add(new LineBreak());
                    target.Add(new Run(new string(' ', depth * 2) +
                                       (list.IsOrdered ? $"{number++}. " : "• ")));
                    AppendContainerBlocks(target, item, depth + 1);
                }
                break;
            case QuoteBlock quote:
                var quoteSpan = new Span { Foreground = new SolidColorBrush(Color.Parse("#94A3B8")) };
                quoteSpan.Inlines.Add(new Run("│ "));
                AppendContainerBlocks(quoteSpan.Inlines, quote, depth + 1);
                target.Add(quoteSpan);
                break;
            case CodeBlock code:
                target.Add(new Span
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    Background = new SolidColorBrush(Color.Parse("#172033")),
                    Inlines = { new Run(code.Lines.ToString().TrimEnd('\r', '\n')) }
                });
                break;
            case ThematicBreakBlock:
                target.Add(new Run("────────────"));
                break;
            case ContainerBlock container:
                AppendContainerBlocks(target, container, depth);
                break;
            case LeafBlock leaf:
                AppendInlineContainer(target, leaf.Inline);
                break;
        }
    }

    private static void AppendContainerBlocks(InlineCollection target, ContainerBlock container, int depth)
    {
        var first = true;
        foreach (var child in container)
        {
            if (!first) target.Add(new LineBreak());
            AppendBlock(target, child, depth);
            first = false;
        }
    }

    private static void AppendInlineContainer(InlineCollection target, ContainerInline? container)
    {
        for (var inline = container?.FirstChild; inline != null; inline = inline.NextSibling)
            AppendInline(target, inline);
    }

    private static void AppendInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                target.Add(new Span
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                    Background = new SolidColorBrush(Color.Parse("#172033")),
                    Inlines = { new Run(code.Content) }
                });
                break;
            case EmphasisInline emphasis:
                Span emphasisSpan = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AppendInlineContainer(emphasisSpan.Inlines, emphasis);
                target.Add(emphasisSpan);
                break;
            case LinkInline link:
                var linkSpan = new Span
                {
                    Foreground = new SolidColorBrush(Color.Parse("#60A5FA")),
                    TextDecorations = Avalonia.Media.TextDecorations.Underline
                };
                AppendInlineContainer(linkSpan.Inlines, link);
                target.Add(linkSpan);
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case ContainerInline nested:
                AppendInlineContainer(target, nested);
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            default:
                target.Add(new Run(inline.ToString() ?? string.Empty));
                break;
        }
    }
}
