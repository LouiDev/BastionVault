using System.Text;

namespace BastionVault.App.Services;

/// <summary>What kind of block a line of Markdown turned into.</summary>
public enum MarkdownBlockKind
{
    /// <summary>A heading; <see cref="MarkdownBlock.Level"/> is 1 to 6.</summary>
    Heading,

    /// <summary>Running text.</summary>
    Paragraph,

    /// <summary>Preformatted text, shown in the mono face; <see cref="MarkdownBlock.Code"/> holds it verbatim.</summary>
    Code,

    /// <summary>A block quote.</summary>
    Quote,

    /// <summary>One list item; <see cref="MarkdownBlock.Level"/> is its nesting depth from 0.</summary>
    ListItem,

    /// <summary>A horizontal rule.</summary>
    Rule,
}

/// <summary>Styles an inline run can carry; several can apply at once.</summary>
[Flags]
public enum MarkdownStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Strong = 1,

    /// <summary>Italic.</summary>
    Emphasis = 2,

    /// <summary>Inline code, in the mono face.</summary>
    Code = 4,

    /// <summary>Link text. The destination is dropped: nothing in the pane navigates anywhere.</summary>
    Link = 8,

    /// <summary>Struck through.</summary>
    Strike = 16,
}

/// <summary>A run of text with one set of styles, or a hard line break.</summary>
/// <param name="Text">The characters to show; empty for a line break.</param>
/// <param name="Style">The styles that apply.</param>
/// <param name="IsLineBreak">True for a hard line break inside a block.</param>
public sealed record MarkdownInline(string Text, MarkdownStyle Style = MarkdownStyle.None, bool IsLineBreak = false)
{
    /// <summary>The one line-break instance.</summary>
    public static MarkdownInline LineBreak { get; } = new(string.Empty, MarkdownStyle.None, IsLineBreak: true);
}

/// <summary>One block of a parsed document.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="Inlines">Its text, for everything but <see cref="MarkdownBlockKind.Code"/> and <see cref="MarkdownBlockKind.Rule"/>.</param>
/// <param name="Level">Heading level (1 to 6) or list nesting depth (from 0).</param>
/// <param name="Ordered">For a list item: numbered rather than bulleted.</param>
/// <param name="Code">For a code block: the verbatim text, lines joined by <c>\n</c>.</param>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInline> Inlines,
    int Level = 0,
    bool Ordered = false,
    string? Code = null);

/// <summary>
/// Turns Markdown into blocks and styled runs for the preview pane. It understands the everyday subset:
/// ATX and setext headings, paragraphs, hard breaks, fenced and indented code, block quotes, bulleted
/// and numbered lists with nesting by indentation, rules, emphasis, strong, strikethrough, inline code,
/// links and images (shown as their text; nothing is fetched or followed), and pipe tables (kept as
/// preformatted text so the columns line up). Everything else is shown as the text it is. There is no
/// HTML and no raw output of any kind: the result is only ever drawn as runs.
/// </summary>
/// <remarks>
/// The input comes from a vault, so it is attacker-chosen. The parser is linear in the input apart from
/// delimiter matching inside one paragraph, which is bounded by <see cref="MaxInlineLength"/>: a paragraph
/// longer than that is shown as plain text. <see cref="MaxBlocks"/> bounds the output.
/// </remarks>
public static class MarkdownParser
{
    /// <summary>A paragraph longer than this (in characters) is not styled, only shown.</summary>
    public const int MaxInlineLength = 64 * 1024;

    /// <summary>More blocks than this are dropped; the pane says so through the last block.</summary>
    public const int MaxBlocks = 20_000;

    private const int MaxListDepth = 6;

    /// <summary>
    /// Bounds the work spent on markup that never closes: after this many failed searches for a closing
    /// delimiter or bracket in one block, the rest of the block is shown as plain text.
    /// </summary>
    private const int MaxFailedSearches = 64;

    private sealed class InlineBudget
    {
        public int FailedSearches { get; set; }

        public bool Exhausted => FailedSearches >= MaxFailedSearches;
    }

    /// <summary>Parses a document.</summary>
    /// <param name="text">The Markdown source; <see langword="null"/> is an empty document.</param>
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();
        var quote = new List<string>();
        var table = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Paragraph, ParseParagraph(paragraph)));
                paragraph.Clear();
            }
        }

        void FlushQuote()
        {
            if (quote.Count > 0)
            {
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Quote, ParseParagraph(quote)));
                quote.Clear();
            }
        }

        void FlushTable()
        {
            if (table.Count > 0)
            {
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Code, [], Code: string.Join('\n', table)));
                table.Clear();
            }
        }

        void FlushAll()
        {
            FlushParagraph();
            FlushQuote();
            FlushTable();
        }

        for (int i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (trimmed.Length == 0)
            {
                FlushAll();
                continue;
            }

            // Fenced code: everything up to the closing fence, verbatim.
            if (indent <= 3 && IsFence(trimmed, out char fence, out int fenceLength))
            {
                FlushAll();
                var code = new List<string>();
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string candidate = lines[j].TrimStart();
                    if (IsFence(candidate, out char closing, out int closingLength) && closing == fence && closingLength >= fenceLength && candidate.Trim().Length == closingLength)
                    {
                        break;
                    }

                    code.Add(lines[j]);
                }

                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Code, [], Code: string.Join('\n', code)));
                i = j;
                continue;
            }

            // Pipe tables are kept as they are, so the columns still line up.
            if (indent <= 3 && trimmed[0] == '|')
            {
                FlushParagraph();
                FlushQuote();
                table.Add(trimmed);
                continue;
            }

            FlushTable();

            // Setext heading: a one-line paragraph underlined with = or -.
            if (paragraph.Count == 1 && quote.Count == 0 && indent <= 3 && IsSetextUnderline(trimmed, out int setextLevel))
            {
                string heading = paragraph[0];
                paragraph.Clear();
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(heading.Trim()), setextLevel));
                continue;
            }

            if (indent <= 3 && IsRule(trimmed))
            {
                FlushAll();
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Rule, []));
                continue;
            }

            if (indent <= 3 && TryHeading(trimmed, out int level, out string headingText))
            {
                FlushAll();
                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(headingText), level));
                continue;
            }

            if (indent <= 3 && trimmed[0] == '>')
            {
                FlushParagraph();
                string inner = trimmed[1..];
                if (inner.StartsWith(' '))
                {
                    inner = inner[1..];
                }

                quote.Add(inner);
                continue;
            }

            FlushQuote();

            if (TryListItem(trimmed, out bool ordered, out string itemText))
            {
                FlushParagraph();
                int depth = Math.Min(indent / 2, MaxListDepth);
                var item = new List<string> { itemText };

                // Lazy continuation: following lines that are not blank and not a new block belong to the item.
                while (i + 1 < lines.Length)
                {
                    string next = lines[i + 1];
                    string nextTrimmed = next.TrimStart();
                    int nextIndent = next.Length - nextTrimmed.Length;
                    if (nextTrimmed.Length == 0 || TryListItem(nextTrimmed, out _, out _) || IsFence(nextTrimmed, out _, out _)
                        || (nextIndent <= 3 && (IsRule(nextTrimmed) || TryHeading(nextTrimmed, out _, out _) || nextTrimmed[0] is '>' or '|')))
                    {
                        break;
                    }

                    item.Add(nextTrimmed);
                    i++;
                }

                Add(blocks, new MarkdownBlock(MarkdownBlockKind.ListItem, ParseParagraph(item), depth, ordered));
                continue;
            }

            // Indented code: four spaces, outside a paragraph.
            if (indent >= 4 && paragraph.Count == 0)
            {
                var code = new List<string> { line[4..] };
                while (i + 1 < lines.Length && (lines[i + 1].Length == 0 || lines[i + 1].StartsWith("    ", StringComparison.Ordinal)))
                {
                    string next = lines[i + 1];
                    if (next.Length == 0 && (i + 2 >= lines.Length || !lines[i + 2].StartsWith("    ", StringComparison.Ordinal)))
                    {
                        break;
                    }

                    code.Add(next.Length >= 4 ? next[4..] : string.Empty);
                    i++;
                }

                Add(blocks, new MarkdownBlock(MarkdownBlockKind.Code, [], Code: string.Join('\n', code)));
                continue;
            }

            paragraph.Add(trimmed);
        }

        FlushAll();
        return blocks;
    }

    private static void Add(List<MarkdownBlock> blocks, MarkdownBlock block)
    {
        if (blocks.Count < MaxBlocks)
        {
            blocks.Add(block);
        }
    }

    private static bool IsFence(string trimmed, out char fence, out int length)
    {
        fence = '\0';
        length = 0;
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return false;
        }

        fence = trimmed[0];
        while (length < trimmed.Length && trimmed[length] == fence)
        {
            length++;
        }

        return length >= 3;
    }

    private static bool IsSetextUnderline(string trimmed, out int level)
    {
        string compact = trimmed.TrimEnd();
        level = 0;
        if (compact.Length == 0)
        {
            return false;
        }

        char mark = compact[0];
        if (mark != '=' && mark != '-')
        {
            return false;
        }

        foreach (char c in compact)
        {
            if (c != mark)
            {
                return false;
            }
        }

        level = mark == '=' ? 1 : 2;
        return true;
    }

    private static bool IsRule(string trimmed)
    {
        char mark = trimmed[0];
        if (mark != '-' && mark != '*' && mark != '_')
        {
            return false;
        }

        int count = 0;
        foreach (char c in trimmed)
        {
            if (c == mark)
            {
                count++;
            }
            else if (c != ' ')
            {
                return false;
            }
        }

        return count >= 3;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = string.Empty;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        if (level == 0 || level > 6 || (level < trimmed.Length && trimmed[level] != ' '))
        {
            level = 0;
            return false;
        }

        text = trimmed[level..].Trim();

        // Closing hashes are decoration: "## Title ##".
        int end = text.Length;
        while (end > 0 && text[end - 1] == '#')
        {
            end--;
        }

        if (end < text.Length && (end == 0 || text[end - 1] == ' '))
        {
            text = text[..end].TrimEnd();
        }

        return true;
    }

    private static bool TryListItem(string trimmed, out bool ordered, out string text)
    {
        ordered = false;
        text = string.Empty;

        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            text = trimmed[2..].TrimStart();
            return true;
        }

        int digits = 0;
        while (digits < trimmed.Length && digits < 9 && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
        {
            ordered = true;
            text = trimmed[(digits + 2)..].TrimStart();
            return true;
        }

        return false;
    }

    /// <summary>Joins paragraph lines with soft breaks, keeping hard breaks (two trailing spaces or a backslash).</summary>
    private static IReadOnlyList<MarkdownInline> ParseParagraph(List<string> lines)
    {
        var builder = new StringBuilder();
        var result = new List<MarkdownInline>();

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            bool hardBreak = i < lines.Count - 1 && (line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith('\\'));
            builder.Append(hardBreak ? line.TrimEnd(' ', '\\') : line.TrimEnd());

            if (hardBreak)
            {
                result.AddRange(ParseInlines(builder.ToString()));
                result.Add(MarkdownInline.LineBreak);
                builder.Clear();
            }
            else if (i < lines.Count - 1)
            {
                builder.Append(' ');
            }
        }

        if (builder.Length > 0)
        {
            result.AddRange(ParseInlines(builder.ToString()));
        }

        return result;
    }

    /// <summary>Splits one block's text into styled runs.</summary>
    /// <param name="text">The text, without line breaks.</param>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxInlineLength)
        {
            return [new MarkdownInline(text)];
        }

        var runs = new List<MarkdownInline>();
        ParseInlines(text, MarkdownStyle.None, runs, depth: 0, new InlineBudget());
        return Merge(runs);
    }

    private static void ParseInlines(string text, MarkdownStyle style, List<MarkdownInline> runs, int depth, InlineBudget budget)
    {
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length > 0)
            {
                runs.Add(new MarkdownInline(plain.ToString(), style));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // Inline code: a run of backticks closed by a run of the same length.
            if (c == '`')
            {
                int open = CountRun(text, i, '`');
                int close = FindBacktickRun(text, i + open, open);
                if (close >= 0)
                {
                    FlushPlain();
                    string code = text[(i + open)..close];
                    if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0)
                    {
                        code = code[1..^1];
                    }

                    runs.Add(new MarkdownInline(code, style | MarkdownStyle.Code));
                    i = close + open;
                    continue;
                }

                plain.Append(text, i, open);
                i += open;
                continue;
            }

            if (depth < 4 && !budget.Exhausted)
            {
                // Images are shown as their alt text; links as their text. Neither destination is kept.
                if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && TryLink(text, i + 1, out string alt, out int afterImage, budget))
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(alt.Length > 0 ? alt : "image", style | MarkdownStyle.Emphasis));
                    i = afterImage;
                    continue;
                }

                if (c == '[' && TryLink(text, i, out string label, out int afterLink, budget))
                {
                    FlushPlain();
                    ParseInlines(label, style | MarkdownStyle.Link, runs, depth + 1, budget);
                    i = afterLink;
                    continue;
                }

                if (c == '<' && TryAutolink(text, i, out string url, out int afterAutolink, budget))
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(url, style | MarkdownStyle.Link));
                    i = afterAutolink;
                    continue;
                }

                if (c is '*' or '_' or '~' && TryDelimited(text, i, out int prefix, out string inner, out MarkdownStyle added, out int afterDelimited, budget))
                {
                    // An opener longer than what closed it leaves its extra characters as text: "***em*".
                    if (prefix > 0)
                    {
                        plain.Append(text, i, prefix);
                    }

                    FlushPlain();
                    ParseInlines(inner, style | added, runs, depth + 1, budget);
                    i = afterDelimited;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
    }

    private static bool IsEscapable(char c) => c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '~' or '<' or '>';

    private static int CountRun(string text, int start, char c)
    {
        int n = 0;
        while (start + n < text.Length && text[start + n] == c)
        {
            n++;
        }

        return n;
    }

    private static int FindBacktickRun(string text, int from, int length)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            int run = CountRun(text, i, '`');
            if (run == length)
            {
                return i;
            }

            i += run;
        }

        return -1;
    }

    /// <summary>Matches <c>[label](destination)</c> at <paramref name="start"/>; the destination is discarded.</summary>
    private static bool TryLink(string text, int start, out string label, out int end, InlineBudget budget)
    {
        label = string.Empty;
        end = start;

        int close = FindClosing(text, start + 1, '[', ']');
        if (close < 0)
        {
            budget.FailedSearches++;
            return false;
        }

        if (close + 1 >= text.Length || text[close + 1] != '(')
        {
            return false;
        }

        int closeParen = FindClosing(text, close + 2, '(', ')');
        if (closeParen < 0)
        {
            budget.FailedSearches++;
            return false;
        }

        label = text[(start + 1)..close];
        end = closeParen + 1;
        return true;
    }

    /// <summary>Matches <c>&lt;scheme:rest&gt;</c> with no spaces inside.</summary>
    private static bool TryAutolink(string text, int start, out string url, out int end, InlineBudget budget)
    {
        url = string.Empty;
        end = start;
        int close = text.IndexOf('>', start + 1);
        if (close < 0)
        {
            budget.FailedSearches++;
            return false;
        }

        string inner = text[(start + 1)..close];
        int colon = inner.IndexOf(':', StringComparison.Ordinal);
        if (colon < 2 || inner.Contains(' ', StringComparison.Ordinal) || inner.Contains('<', StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < colon; i++)
        {
            if (!char.IsAsciiLetterOrDigit(inner[i]) && inner[i] is not ('+' or '.' or '-'))
            {
                return false;
            }
        }

        url = inner;
        end = close + 1;
        return true;
    }

    private static int FindClosing(string text, int from, char open, char close)
    {
        int level = 0;
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\')
            {
                i++;
            }
            else if (c == open)
            {
                level++;
            }
            else if (c == close)
            {
                if (level == 0)
                {
                    return i;
                }

                level--;
            }
        }

        return -1;
    }

    /// <summary>
    /// Matches <c>***both***</c>, <c>**strong**</c>, <c>__strong__</c>, <c>*emphasis*</c>, <c>_emphasis_</c> and
    /// <c>~~strike~~</c> at <paramref name="start"/>. An opener must be followed by a non-space and a closer
    /// preceded by one; underscores inside words do not open anything, so snake_case stays as it is. The
    /// longest match is tried first; an opener longer than its closer leaves <paramref name="prefix"/>
    /// characters as text.
    /// </summary>
    private static bool TryDelimited(string text, int start, out int prefix, out string inner, out MarkdownStyle style, out int end, InlineBudget budget)
    {
        prefix = 0;
        inner = string.Empty;
        style = MarkdownStyle.None;
        end = start;

        char mark = text[start];
        int run = CountRun(text, start, mark);
        if (mark == '~' && run < 2)
        {
            return false;
        }

        if (mark == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1]))
        {
            return false;
        }

        int contentStart = start + run;
        if (contentStart >= text.Length || text[contentStart] == ' ')
        {
            return false;
        }

        int minLength = mark == '~' ? 2 : 1;
        int close = FindDelimiter(text, contentStart, mark, minLength);
        if (close < 0)
        {
            budget.FailedSearches++;
            return false;
        }

        int closerRun = CountRun(text, close, mark);
        int candidate = mark == '~' ? 2 : Math.Min(Math.Min(run, closerRun), 3);

        prefix = run - candidate;
        inner = text[contentStart..close];
        style = mark == '~' ? MarkdownStyle.Strike : candidate switch
        {
            3 => MarkdownStyle.Strong | MarkdownStyle.Emphasis,
            2 => MarkdownStyle.Strong,
            _ => MarkdownStyle.Emphasis,
        };
        end = close + candidate;
        return true;
    }

    /// <summary>
    /// Finds the run of <paramref name="mark"/>s that closes a span opened just before <paramref name="from"/>,
    /// skipping code spans and escapes. A run that can only open (preceded by a space, followed by text)
    /// starts an inner span and is remembered; a run that can close pairs with the innermost open span
    /// first, so <c>*a **b** c*</c> closes at the last star. Only a closer with nothing left to pair is the
    /// answer. Underscores follow the word rules: they neither open after nor close before a letter or digit.
    /// </summary>
    private static int FindDelimiter(string text, int from, char mark, int minLength)
    {
        var inner = new Stack<int>();
        int i = from;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                // Skip a code span so a delimiter inside it does not close anything.
                int run = CountRun(text, i, '`');
                int close = FindBacktickRun(text, i + run, run);
                i = close >= 0 ? close + run : i + run;
                continue;
            }

            if (c != mark)
            {
                i++;
                continue;
            }

            int length = CountRun(text, i, mark);
            int after = i + length;
            bool wordBefore = i > 0 && char.IsLetterOrDigit(text[i - 1]);
            bool wordAfter = after < text.Length && char.IsLetterOrDigit(text[after]);
            bool canClose = length >= minLength && i > from && text[i - 1] != ' ' && (mark != '_' || !wordAfter);
            bool canOpen = length >= minLength && after < text.Length && text[after] != ' ' && (mark != '_' || !wordBefore);

            if (canClose)
            {
                int remaining = length;
                while (remaining > 0 && inner.Count > 0)
                {
                    int open = inner.Pop();
                    int used = Math.Min(open, remaining);
                    remaining -= used;
                    if (open > used)
                    {
                        inner.Push(open - used);
                    }
                }

                if (remaining > 0 && inner.Count == 0)
                {
                    // The part of this run no inner span claimed closes ours.
                    return after - remaining;
                }
            }
            else if (canOpen)
            {
                inner.Push(length);
            }

            i = after;
        }

        return -1;
    }

    /// <summary>Joins neighbouring runs with the same style so the renderer makes fewer elements.</summary>
    private static List<MarkdownInline> Merge(List<MarkdownInline> runs)
    {
        var merged = new List<MarkdownInline>(runs.Count);
        foreach (MarkdownInline run in runs)
        {
            if (run.Text.Length == 0 && !run.IsLineBreak)
            {
                continue;
            }

            if (merged.Count > 0 && !run.IsLineBreak && !merged[^1].IsLineBreak && merged[^1].Style == run.Style)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + run.Text };
            }
            else
            {
                merged.Add(run);
            }
        }

        return merged;
    }
}
