using System.Diagnostics;
using BastionVault.App.Services;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The Markdown subset the preview renders. The input is vault content, so the parser has to come back
/// with something sensible for every string and never take long doing it.
/// </summary>
public sealed class MarkdownParserTests
{
    private static string Plain(MarkdownBlock block) => string.Concat(block.Inlines.Select(i => i.IsLineBreak ? "\n" : i.Text));

    [Fact]
    public void Empty_input_is_an_empty_document()
    {
        Assert.Empty(MarkdownParser.Parse(null));
        Assert.Empty(MarkdownParser.Parse(string.Empty));
        Assert.Empty(MarkdownParser.Parse("\n\n   \n"));
    }

    [Theory]
    [InlineData("# One", 1, "One")]
    [InlineData("###### Six", 6, "Six")]
    [InlineData("## Closed ##", 2, "Closed")]
    [InlineData("##   Padded   ", 2, "Padded")]
    public void Atx_headings_carry_their_level_without_the_hashes(string line, int level, string text)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
        Assert.Equal(level, block.Level);
        Assert.Equal(text, Plain(block));
    }

    [Theory]
    [InlineData("####### Seven")]
    [InlineData("#NoSpace")]
    public void Hashes_that_are_not_a_heading_stay_text(string line)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal(line, Plain(block));
    }

    [Fact]
    public void Setext_headings_underline_a_one_line_paragraph()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("Title\n=====\n\nSub\n---\n\nText\nmore\n---");

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("Title", Plain(blocks[0]));
        Assert.Equal(MarkdownBlockKind.Heading, blocks[1].Kind);
        Assert.Equal(2, blocks[1].Level);

        // A two-line paragraph followed by --- is a paragraph and a rule, not a heading.
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[2].Kind);
        Assert.Equal("Text more", Plain(blocks[2]));
        Assert.Equal(MarkdownBlockKind.Rule, blocks[3].Kind);
    }

    [Fact]
    public void Paragraph_lines_are_joined_and_blank_lines_separate_them()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("one\ntwo\n\nthree");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("one two", Plain(blocks[0]));
        Assert.Equal("three", Plain(blocks[1]));
    }

    [Fact]
    public void Two_trailing_spaces_or_a_backslash_make_a_hard_break()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("first  \nsecond\\\nthird"));

        Assert.Equal("first\nsecond\nthird", Plain(block));
        Assert.Equal(2, block.Inlines.Count(i => i.IsLineBreak));
    }

    [Fact]
    public void Fenced_code_is_kept_verbatim_and_its_info_string_dropped()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("```csharp\nvar x = **not bold**;\n\n  indented\n```\nafter");

        Assert.Equal(MarkdownBlockKind.Code, blocks[0].Kind);
        Assert.Equal("var x = **not bold**;\n\n  indented", blocks[0].Code);
        Assert.Equal("after", Plain(blocks[1]));
    }

    [Fact]
    public void An_unterminated_fence_runs_to_the_end()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("~~~\nnever\nclosed"));

        Assert.Equal(MarkdownBlockKind.Code, block.Kind);
        Assert.Equal("never\nclosed", block.Code);
    }

    [Fact]
    public void Four_spaces_outside_a_paragraph_are_code()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("para\n    not code, continuation\n\n    code line 1\n    code line 2\n\nafter");

        Assert.Equal("para not code, continuation", Plain(blocks[0]));
        Assert.Equal(MarkdownBlockKind.Code, blocks[1].Kind);
        Assert.Equal("code line 1\ncode line 2", blocks[1].Code);
        Assert.Equal("after", Plain(blocks[2]));
    }

    [Fact]
    public void Quotes_join_their_lines_and_keep_inline_styles()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("> quoted *word*\n> continues"));

        Assert.Equal(MarkdownBlockKind.Quote, block.Kind);
        Assert.Equal("quoted word continues", Plain(block));
        Assert.Contains(block.Inlines, i => i.Text == "word" && i.Style == MarkdownStyle.Emphasis);
    }

    [Fact]
    public void Lists_nest_by_indentation_and_know_whether_they_are_numbered()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("- a\n- b\n  continued\n  - b1\n    - b11\n1. one\n2) two\n+ plus\n* star");

        Assert.All(blocks, b => Assert.Equal(MarkdownBlockKind.ListItem, b.Kind));
        Assert.Equal(["a", "b continued", "b1", "b11", "one", "two", "plus", "star"], blocks.Select(Plain));
        Assert.Equal([0, 0, 1, 2, 0, 0, 0, 0], blocks.Select(b => b.Level));
        Assert.Equal([false, false, false, false, true, true, false, false], blocks.Select(b => b.Ordered));
    }

    [Fact]
    public void A_list_item_ends_at_a_blank_line_or_another_block()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("- item\n# Heading\n- second\n\nparagraph");

        Assert.Equal([MarkdownBlockKind.ListItem, MarkdownBlockKind.Heading, MarkdownBlockKind.ListItem, MarkdownBlockKind.Paragraph], blocks.Select(b => b.Kind));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    [InlineData("  ----------")]
    public void Rules_come_in_three_characters(string line)
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse(line));

        Assert.Equal(MarkdownBlockKind.Rule, block.Kind);
    }

    [Fact]
    public void Pipe_tables_are_kept_as_preformatted_text()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("| a | b |\n|---|---|\n| 1 | 2 |\n\nafter");

        Assert.Equal(MarkdownBlockKind.Code, blocks[0].Kind);
        Assert.Equal("| a | b |\n|---|---|\n| 1 | 2 |", blocks[0].Code);
        Assert.Equal("after", Plain(blocks[1]));
    }

    [Fact]
    public void Emphasis_strong_strike_and_code_become_styled_runs()
    {
        IReadOnlyList<MarkdownInline> runs = MarkdownParser.ParseInlines("plain *em* **strong** ***both*** ~~gone~~ `co*de` _under_ __dunder__");

        Assert.Equal(
            [
                ("plain ", MarkdownStyle.None),
                ("em", MarkdownStyle.Emphasis),
                (" ", MarkdownStyle.None),
                ("strong", MarkdownStyle.Strong),
                (" ", MarkdownStyle.None),
                ("both", MarkdownStyle.Strong | MarkdownStyle.Emphasis),
                (" ", MarkdownStyle.None),
                ("gone", MarkdownStyle.Strike),
                (" ", MarkdownStyle.None),
                ("co*de", MarkdownStyle.Code),
                (" ", MarkdownStyle.None),
                ("under", MarkdownStyle.Emphasis),
                (" ", MarkdownStyle.None),
                ("dunder", MarkdownStyle.Strong),
            ],
            runs.Select(r => (r.Text, r.Style)));
    }

    [Fact]
    public void Delimiters_that_do_not_close_or_sit_inside_words_stay_text()
    {
        Assert.Equal("a * b * c", string.Concat(MarkdownParser.ParseInlines("a * b * c").Select(r => r.Text)));
        Assert.All(MarkdownParser.ParseInlines("a * b * c"), r => Assert.Equal(MarkdownStyle.None, r.Style));

        IReadOnlyList<MarkdownInline> snake = MarkdownParser.ParseInlines("snake_case_name and _real_");
        Assert.Equal("snake_case_name and real", string.Concat(snake.Select(r => r.Text)));
        Assert.Equal(MarkdownStyle.Emphasis, snake[^1].Style);

        Assert.Equal("**unclosed", Assert.Single(MarkdownParser.ParseInlines("**unclosed")).Text);
    }

    [Fact]
    public void Backslashes_escape_markup_and_code_spans_keep_everything()
    {
        Assert.Equal("*literal* and \\ back", Assert.Single(MarkdownParser.ParseInlines(@"\*literal\* and \\ back")).Text);

        IReadOnlyList<MarkdownInline> runs = MarkdownParser.ParseInlines("`` a ` b `` and ` x `");
        Assert.Equal(("a ` b", MarkdownStyle.Code), (runs[0].Text, runs[0].Style));
        Assert.Equal(("x", MarkdownStyle.Code), (runs[2].Text, runs[2].Style));
    }

    [Fact]
    public void Links_and_images_show_their_text_and_drop_their_destination()
    {
        IReadOnlyList<MarkdownInline> runs = MarkdownParser.ParseInlines("see [the *docs*](https://example.test/x?y=1 \"t\") and ![alt text](img.png) or <https://a.test/b>");

        Assert.Equal(
            [
                ("see ", MarkdownStyle.None),
                ("the ", MarkdownStyle.Link),
                ("docs", MarkdownStyle.Link | MarkdownStyle.Emphasis),
                (" and ", MarkdownStyle.None),
                ("alt text", MarkdownStyle.Emphasis),
                (" or ", MarkdownStyle.None),
                ("https://a.test/b", MarkdownStyle.Link),
            ],
            runs.Select(r => (r.Text, r.Style)));
        Assert.DoesNotContain(runs, r => r.Text.Contains("example.test", StringComparison.Ordinal));
        Assert.DoesNotContain(runs, r => r.Text.Contains("img.png", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_and_unknown_syntax_are_shown_as_the_text_they_are()
    {
        MarkdownBlock block = Assert.Single(MarkdownParser.Parse("<script>alert(1)</script> [x] {y} <not a link>"));

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal("<script>alert(1)</script> [x] {y} <not a link>", Plain(block));
    }

    [Fact]
    public void Line_endings_and_tabs_are_normalised()
    {
        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse("# A\r\n\r\n\tcode\r\n");

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(MarkdownBlockKind.Code, blocks[1].Kind);
        Assert.Equal("code", blocks[1].Code);
    }

    [Fact]
    public void Longer_openers_than_closers_leave_the_extra_characters_as_text()
    {
        IReadOnlyList<MarkdownInline> runs = MarkdownParser.ParseInlines("***em* and **a * b**");

        Assert.Equal(
            [
                ("**", MarkdownStyle.None),
                ("em", MarkdownStyle.Emphasis),
                (" and ", MarkdownStyle.None),
                ("a * b", MarkdownStyle.Strong),
            ],
            runs.Select(r => (r.Text, r.Style)));
    }

    [Fact]
    public void Pathological_markup_finishes_quickly_and_comes_back_as_text()
    {
        // Each one stays under the per-paragraph styling cap, so the parser does try to style it.
        string unclosedEmphasis = "x " + string.Concat(Enumerable.Repeat("*a ", 20_000));
        string unclosedBrackets = string.Concat(Enumerable.Repeat("[x", 30_000));
        string unclosedAutolinks = string.Concat(Enumerable.Repeat("<a:", 20_000));

        // And one over the cap, which is shown without a styling pass at all.
        string hugeParagraph = "a " + new string('*', 200_000);

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<MarkdownBlock> first = MarkdownParser.Parse(unclosedEmphasis);
        IReadOnlyList<MarkdownBlock> second = MarkdownParser.Parse(unclosedBrackets);
        IReadOnlyList<MarkdownBlock> third = MarkdownParser.Parse(unclosedAutolinks);
        IReadOnlyList<MarkdownBlock> fourth = MarkdownParser.Parse(hugeParagraph);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
        Assert.Equal(unclosedEmphasis.TrimEnd(), Plain(Assert.Single(first)));
        Assert.Equal(unclosedBrackets, Plain(Assert.Single(second)));
        Assert.Equal(unclosedAutolinks, Plain(Assert.Single(third)));
        Assert.Equal(hugeParagraph, Plain(Assert.Single(fourth)));
    }

    [Fact]
    public void The_block_count_is_capped()
    {
        string manyItems = string.Concat(Enumerable.Repeat("- x\n", MarkdownParser.MaxBlocks + 500));

        IReadOnlyList<MarkdownBlock> blocks = MarkdownParser.Parse(manyItems);

        Assert.Equal(MarkdownParser.MaxBlocks, blocks.Count);
    }
}
