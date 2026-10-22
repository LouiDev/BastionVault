using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BastionVault.App.Services;

namespace BastionVault.App.Views;

/// <summary>
/// Renders the blocks <see cref="MarkdownParser"/> produces as a <see cref="FlowDocument"/> in the theme's
/// faces and brushes. Every brush and family is a resource reference, so the high-contrast swap repaints
/// a document that is already on screen. Selection, the toolbar and the context menu are off: the pane
/// shows, it does not edit, and vault content never reaches the OS clipboard from here (UI-CONTRACT.md
/// section 1.11). Links are drawn as text; nothing navigates.
/// </summary>
public sealed class MarkdownPreview : FlowDocumentScrollViewer
{
    /// <summary>Identifies the <see cref="Markdown"/> dependency property.</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPreview), new PropertyMetadata(null, OnMarkdownChanged));

    private static readonly Thickness ParagraphMargin = new(0, 0, 0, 8);

    /// <summary>Creates the viewer with the pane's reading settings.</summary>
    public MarkdownPreview()
    {
        IsSelectionEnabled = false;
        IsToolBarVisible = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Focusable = false;
        ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>The Markdown source to show, or <see langword="null"/> for an empty document.</summary>
    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (MarkdownPreview)d;
        viewer.Document = e.NewValue is string source && source.Length > 0 ? Build(MarkdownParser.Parse(source)) : null;
    }

    /// <summary>Builds the document for a parsed Markdown block list.</summary>
    /// <param name="blocks">The blocks, in order.</param>
    public static FlowDocument Build(IReadOnlyList<MarkdownBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var document = new FlowDocument
        {
            PagePadding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = false,
            IsHyphenationEnabled = false,
        };
        document.SetResourceReference(TextElement.FontFamilyProperty, "Font.Text");
        document.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextPrimary");

        var lists = new Stack<(int Level, List List)>();

        void CloseListsDownTo(int level)
        {
            while (lists.Count > 0 && lists.Peek().Level > level)
            {
                lists.Pop();
            }
        }

        foreach (MarkdownBlock block in blocks)
        {
            if (block.Kind == MarkdownBlockKind.ListItem)
            {
                CloseListsDownTo(block.Level);
                if (lists.Count == 0 || lists.Peek().Level < block.Level || lists.Peek().List.MarkerStyle != MarkerFor(block.Ordered))
                {
                    var list = new List
                    {
                        MarkerStyle = MarkerFor(block.Ordered),
                        Margin = lists.Count == 0 ? ParagraphMargin : new Thickness(0),
                        Padding = new Thickness(20, 0, 0, 0),
                    };

                    if (lists.Count > 0 && lists.Peek().Level < block.Level)
                    {
                        ListItem parent = LastItemOf(lists.Peek().List);
                        parent.Blocks.Add(list);
                    }
                    else
                    {
                        if (lists.Count > 0)
                        {
                            lists.Pop();
                        }

                        document.Blocks.Add(list);
                    }

                    lists.Push((block.Level, list));
                }

                var item = new ListItem(Paragraph(block.Inlines, new Thickness(0, 0, 0, 2)));
                lists.Peek().List.ListItems.Add(item);
                continue;
            }

            lists.Clear();
            document.Blocks.Add(block.Kind switch
            {
                MarkdownBlockKind.Heading => Heading(block),
                MarkdownBlockKind.Code => Code(block.Code ?? string.Empty),
                MarkdownBlockKind.Quote => Quote(block.Inlines),
                MarkdownBlockKind.Rule => Rule(),
                _ => Paragraph(block.Inlines, ParagraphMargin),
            });
        }

        return document;
    }

    private static TextMarkerStyle MarkerFor(bool ordered) => ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

    private static ListItem LastItemOf(List list)
    {
        if (list.ListItems.LastListItem is { } last)
        {
            return last;
        }

        var item = new ListItem();
        list.ListItems.Add(item);
        return item;
    }

    private static Paragraph Heading(MarkdownBlock block)
    {
        (double size, double line, string family) = block.Level switch
        {
            1 => (20d, 28d, "Font.Display"),
            2 => (16d, 22d, "Font.Text"),
            _ => (13d, 20d, "Font.Text"),
        };

        Paragraph paragraph = Paragraph(block.Inlines, new Thickness(0, block.Level <= 2 ? 12 : 8, 0, 4));
        paragraph.FontSize = size;
        paragraph.LineHeight = line;
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, family);
        return paragraph;
    }

    private static Paragraph Code(string code)
    {
        var paragraph = new Paragraph
        {
            Margin = ParagraphMargin,
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            LineHeight = 18,
        };
        paragraph.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextMono");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.StrokeDivider");
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "Brush.Bg2");

        string[] lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        return paragraph;
    }

    private static Paragraph Quote(IReadOnlyList<MarkdownInline> inlines)
    {
        Paragraph paragraph = Paragraph(inlines, ParagraphMargin);
        paragraph.Padding = new Thickness(10, 0, 0, 0);
        paragraph.BorderThickness = new Thickness(2, 0, 0, 0);
        paragraph.SetResourceReference(Block.BorderBrushProperty, "Brush.StrokeControl");
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        return paragraph;
    }

    private static Block Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.StrokeDivider");
        return new BlockUIContainer(line) { Margin = ParagraphMargin };
    }

    private static Paragraph Paragraph(IReadOnlyList<MarkdownInline> inlines, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        foreach (MarkdownInline inline in inlines)
        {
            if (inline.IsLineBreak)
            {
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }

            paragraph.Inlines.Add(Run(inline));
        }

        return paragraph;
    }

    private static Run Run(MarkdownInline inline)
    {
        var run = new Run(inline.Text);
        MarkdownStyle style = inline.Style;

        if (style.HasFlag(MarkdownStyle.Strong))
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (style.HasFlag(MarkdownStyle.Emphasis))
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (style.HasFlag(MarkdownStyle.Code))
        {
            run.FontSize = 12;
            run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono");
            run.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextMono");
            run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Bg2");
        }

        var decorations = new TextDecorationCollection();
        if (style.HasFlag(MarkdownStyle.Link))
        {
            decorations.Add(TextDecorations.Underline);
            run.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextSecondary");
        }

        if (style.HasFlag(MarkdownStyle.Strike))
        {
            decorations.Add(TextDecorations.Strikethrough);
        }

        if (decorations.Count > 0)
        {
            run.TextDecorations = decorations;
        }

        return run;
    }
}
