using System;
using System.Linq;
using Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Documents;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Data;
using global::Avalonia.Layout;
using global::Avalonia.Markup.Xaml.MarkupExtensions;
using global::Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Heron.Pregel.Avalonia.Views;

/// <summary>Native document layout for artifact reports, including Markdown tables.</summary>
public sealed class AutomationArtifactMarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<AutomationArtifactMarkdownView, string?>(nameof(Markdown));
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().Build();

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public AutomationArtifactMarkdownView() => Spacing = 14;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty && IsVisible) Render(change.NewValue as string);
        else if (change.Property == IsVisibleProperty && IsVisible) Render(Markdown);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible) Render(Markdown);
    }

    private void Render(string? markdown)
    {
        try
        {
            // Build the document before replacing the visible content. A binding callback
            // must not leave an empty panel if parsing or creating a block fails.
            var blocks = Markdig.Markdown.Parse(markdown ?? "", Pipeline).Select(RenderBlock).ToArray();
            Children.Clear();
            Children.AddRange(blocks);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "[AutomationArtifacts] Markdown preview rendering failed");
            Children.Clear();
            var notice = new TextBlock { Text = "Markdown 预览失败，以下显示原文。", TextWrapping = TextWrapping.Wrap };
            notice.Classes.Add("textSecondary");
            var source = new SelectableTextBlock { Text = markdown ?? "", TextWrapping = TextWrapping.Wrap };
            source.Classes.Add("textBody");
            Children.Add(notice);
            Children.Add(source);
        }
    }

    private static Control RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = InlineText(heading.Inline);
                title.FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                title.LineHeight = title.FontSize + 10;
                title.FontWeight = FontWeight.SemiBold;
                title.Margin = new Thickness(0, 10, 0, 2);
                return title;
            case ParagraphBlock paragraph:
                return InlineText(paragraph.Inline);
            case Table table:
                return RenderTable(table);
            case CodeBlock code:
                var codeText = new SelectableTextBlock { Text = code.Lines.ToString().TrimEnd('\r', '\n'), TextWrapping = TextWrapping.NoWrap };
                codeText.Classes.Add("textCode");
                return Surface(new ScrollViewer { Content = codeText, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, "BgSurfaceSubtleBrush", 14);
            case ListBlock list:
                var items = new StackPanel { Spacing = 8 };
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
                    var prefix = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 14, LineHeight = 24 };
                    Resource(prefix, TextBlock.ForegroundProperty, "TextDefaultBrush");
                    row.Children.Add(prefix);
                    var content = RenderContainer(item);
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);
                    items.Children.Add(row);
                }
                return items;
            case QuoteBlock quote:
                var quoted = Surface(RenderContainer(quote), "BgSurfaceSubtleBrush", 12);
                quoted.BorderThickness = new Thickness(3, 0, 0, 0);
                Resource(quoted, Border.BorderBrushProperty, "BorderDefaultBrush");
                return quoted;
            case ThematicBreakBlock:
                var rule = new Border { Height = 1, Margin = new Thickness(0, 6) };
                Resource(rule, Border.BackgroundProperty, "BorderSubtleBrush");
                return rule;
            case ContainerBlock container:
                return RenderContainer(container);
            case LeafBlock leaf:
                return InlineText(leaf.Inline);
            default:
                return new TextBlock();
        }
    }

    private static StackPanel RenderContainer(ContainerBlock container)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var block in container) content.Children.Add(RenderBlock(block));
        return content;
    }

    private static Control RenderTable(Table table)
    {
        var grid = new Grid();
        foreach (var _ in table.ColumnDefinitions) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var nextColumn = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                // Parsed pipe-table cells can leave ColumnIndex at -1. Resolve their
                // position from row order instead of passing the sentinel to Grid.
                var column = cell.ColumnIndex >= 0 ? cell.ColumnIndex : nextColumn;
                var span = Math.Max(1, cell.ColumnSpan);
                while (grid.ColumnDefinitions.Count < column + span)
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                nextColumn = column + span;
                var content = RenderContainer(cell);
                content.MinWidth = 90;
                content.MaxWidth = 460;
                content.VerticalAlignment = VerticalAlignment.Top;
                if (row.IsHeader)
                    foreach (var text in content.Children.OfType<SelectableTextBlock>()) text.FontWeight = FontWeight.SemiBold;
                var border = Surface(content, row.IsHeader || rowIndex % 2 == 0 ? "BgSurfaceSubtleBrush" : "BgSurfaceBrush", 10);
                border.CornerRadius = new CornerRadius(0);
                border.BorderThickness = new Thickness(0, 0, 1, 1);
                Resource(border, Border.BorderBrushProperty, "BorderSubtleBrush");
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, column);
                Grid.SetColumnSpan(border, span);
                grid.Children.Add(border);
            }
            rowIndex++;
        }
        var frame = Surface(grid, "BgSurfaceBrush", 0);
        frame.HorizontalAlignment = HorizontalAlignment.Left;
        frame.BorderThickness = new Thickness(1);
        Resource(frame, Border.BorderBrushProperty, "BorderSubtleBrush");
        return new ScrollViewer { Content = frame, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static SelectableTextBlock InlineText(ContainerInline? inline)
    {
        var text = new SelectableTextBlock { FontSize = 14, LineHeight = 24, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        Resource(text, TextBlock.ForegroundProperty, "TextDefaultBrush");
        AppendInline(text.Inlines, inline);
        return text;
    }

    private static void AppendInline(InlineCollection target, ContainerInline? container)
    {
        for (var inline = container?.FirstChild; inline != null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    var codeSpan = new Span { FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), Inlines = { new Run(code.Content) } };
                    Resource(codeSpan, TextElement.BackgroundProperty, "BgSurfaceSubtleBrush");
                    target.Add(codeSpan);
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AppendInline(styled.Inlines, emphasis);
                    target.Add(styled);
                    break;
                case LinkInline link:
                    var linkSpan = new Span { TextDecorations = TextDecorations.Underline };
                    Resource(linkSpan, TextElement.ForegroundProperty, "TextLinkBrush");
                    AppendInline(linkSpan.Inlines, link);
                    target.Add(linkSpan);
                    break;
                case AutolinkInline link:
                    var address = new Span { TextDecorations = TextDecorations.Underline };
                    Resource(address, TextElement.ForegroundProperty, "TextLinkBrush");
                    address.Inlines.Add(new Run(link.Url));
                    target.Add(address);
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case ContainerInline nested:
                    AppendInline(target, nested);
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
            }
        }
    }

    private static Border Surface(Control child, string background, double padding)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(6) };
        Resource(border, Border.BackgroundProperty, background);
        return border;
    }

    private static void Resource(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));
}
