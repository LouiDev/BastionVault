using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using BastionVault.App.Services;
using BastionVault.App.Views;

namespace BastionVault.App.Tests;

/// <summary>
/// The FlowDocument the Markdown preview builds: the parser's blocks land as the right elements, lists
/// nest, and nothing in the document is a hyperlink or anything else that could navigate.
/// </summary>
public sealed class MarkdownPreviewTests
{
    private const string Sample = """
        # Title

        Intro with **bold**, a [link](https://example.test) and `code`.

        - one
          - nested
        - two

        1. first
        2. second

        > quoted

        ```
        raw
        ```

        ---
        """;

    [Fact]
    public void The_sample_document_builds_with_the_expected_blocks_and_no_hyperlinks()
    {
        (List<string> kinds, bool anyHyperlink, int nestedLists, Exception? escaped) = OnStaThread(() =>
        {
            FlowDocument document = MarkdownPreview.Build(MarkdownParser.Parse(Sample));

            var kinds = new List<string>();
            int nestedLists = 0;
            foreach (Block block in document.Blocks)
            {
                kinds.Add(block.GetType().Name);
                if (block is List list)
                {
                    foreach (ListItem item in list.ListItems)
                    {
                        nestedLists += item.Blocks.OfType<List>().Count();
                    }
                }
            }

            bool anyHyperlink = AllInlines(document).OfType<Hyperlink>().Any();
            return (kinds, anyHyperlink, nestedLists, null);
        });

        Assert.Null(escaped);
        Assert.Equal(["Paragraph", "Paragraph", "List", "List", "Paragraph", "Paragraph", "BlockUIContainer"], kinds);
        Assert.Equal(1, nestedLists);
        Assert.False(anyHyperlink, "links are drawn as text; nothing in the pane navigates");
    }

    [Fact]
    public void Headings_are_heavier_and_larger_than_body_text()
    {
        (List<string> _, bool _, int _, Exception? escaped) = OnStaThread(() =>
        {
            FlowDocument document = MarkdownPreview.Build(MarkdownParser.Parse("# H1\n\nbody\n\n### H3"));
            Block[] blocks = [.. document.Blocks];

            var h1 = Assert.IsType<Paragraph>(blocks[0]);
            var body = Assert.IsType<Paragraph>(blocks[1]);
            var h3 = Assert.IsType<Paragraph>(blocks[2]);

            Assert.Equal(FontWeights.SemiBold, h1.FontWeight);
            Assert.Equal(FontWeights.SemiBold, h3.FontWeight);
            Assert.True(h1.FontSize > body.FontSize, "H1 is larger than body");
            Assert.True(h1.FontSize > h3.FontSize, "H1 is larger than H3");
            return ([], false, 0, null);
        });

        Assert.Null(escaped);
    }

    [Fact]
    public void Styled_runs_carry_their_decorations()
    {
        (List<string> found, bool _, int _, Exception? escaped) = OnStaThread(() =>
        {
            FlowDocument document = MarkdownPreview.Build(MarkdownParser.Parse("**b** *i* ~~s~~ [l](x) `c`"));
            var found = new List<string>();
            foreach (Run run in AllInlines(document).OfType<Run>())
            {
                string text = run.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                string marks = string.Concat(
                    run.FontWeight == FontWeights.SemiBold ? "B" : string.Empty,
                    run.FontStyle == FontStyles.Italic ? "I" : string.Empty,
                    run.TextDecorations?.Contains(TextDecorations.Strikethrough[0]) == true ? "S" : string.Empty,
                    run.TextDecorations?.Contains(TextDecorations.Underline[0]) == true ? "U" : string.Empty,
                    run.FontSize == 12 ? "M" : string.Empty);
                found.Add($"{text}:{marks}");
            }

            return (found, false, 0, null);
        });

        Assert.Null(escaped);
        Assert.Equal(["b:B", "i:I", "s:S", "l:U", "c:M"], found);
    }

    private static IEnumerable<Inline> AllInlines(FlowDocument document)
    {
        foreach (Block block in document.Blocks)
        {
            foreach (Inline inline in AllInlines(block))
            {
                yield return inline;
            }
        }
    }

    private static IEnumerable<Inline> AllInlines(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (Inline inline in paragraph.Inlines)
                {
                    yield return inline;
                    if (inline is Span span)
                    {
                        foreach (Inline nested in span.Inlines)
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                    {
                        foreach (Inline inline in AllInlines(child))
                        {
                            yield return inline;
                        }
                    }
                }

                break;
        }
    }

    /// <summary>Runs a probe on a fresh STA thread with its own dispatcher; anything thrown is returned.</summary>
    private static (List<string>, bool, int, Exception?) OnStaThread(Func<(List<string>, bool, int, Exception?)> probe)
    {
        (List<string>, bool, int, Exception?) result = ([], false, 0, null);

        var thread = new Thread(() =>
        {
            try
            {
                result = probe();
            }
            catch (Exception ex)
            {
                result = ([], false, 0, ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA probe did not finish");

        return result;
    }
}
