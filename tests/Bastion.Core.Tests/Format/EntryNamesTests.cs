using System.Text;
using Bastion.Core.Format;

namespace Bastion.Core.Tests.Format;

/// <summary>Covers the name rules of FORMAT.md section 6.1 and the sanitiser of section 6.2.</summary>
public sealed class EntryNamesTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("notes.txt")]
    [InlineData("archive.tar.gz")]
    [InlineData(".gitignore")]
    [InlineData("..two dots inside")]
    [InlineData("CONSOLE")]
    [InlineData("CON1")]
    [InlineData("COM")]
    [InlineData("LPT")]
    [InlineData("\u00DCn\u00EFc\u00F8d\u00E9.txt")]
    [InlineData("\u65E5\u672C\u8A9E")]
    [InlineData("\uD83D\uDE00 emoji")]
    [InlineData("inner space")]
    [InlineData("name (2).txt")]
    public void Validate_AcceptsAValidName(string name)
    {
        NameCheck check = EntryNames.Validate(name);

        Assert.True(check.IsValid, $"\"{name}\" was rejected: {check.Reason}");
        Assert.Null(check.Reason);
        Assert.Null(check.Suggestion);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\u0000b")]
    [InlineData("a\u0001b")]
    [InlineData("a\u001Fb")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u009Fb")]
    [InlineData("a\u200Eb")]
    [InlineData("a\u200Fb")]
    [InlineData("a\u202Ab")]
    [InlineData("a\u202Bb")]
    [InlineData("a\u202Cb")]
    [InlineData("a\u202Db")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2066b")]
    [InlineData("a\u2067b")]
    [InlineData("a\u2068b")]
    [InlineData("a\u2069b")]
    [InlineData("a\uFEFFb")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("\u00A0leading nbsp")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con.txt")]
    [InlineData("PRN")]
    [InlineData("AUX.log")]
    [InlineData("NUL")]
    [InlineData("COM0")]
    [InlineData("COM9.txt")]
    [InlineData("LPT0")]
    [InlineData("lpt9")]
    public void Validate_RejectsAnInvalidName(string name)
    {
        NameCheck check = EntryNames.Validate(name);

        Assert.False(check.IsValid, $"\"{name}\" was accepted.");
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
    }

    [Fact]
    public void Validate_RejectsAnUnpairedSurrogate()
    {
        // Not an InlineData case: xUnit replaces an unpaired surrogate when it serializes theory data.
        Assert.False(EntryNames.Validate("lone\uD800surrogate").IsValid);
        Assert.False(EntryNames.Validate("trailing\uDC00low").IsValid);
        Assert.True(EntryNames.Validate("paired\uD83D\uDE00").IsValid);
    }

    [Fact]
    public void Validate_AcceptsExactly255CodeUnits()
    {
        Assert.True(EntryNames.Validate(new string('a', 255)).IsValid);
        Assert.False(EntryNames.Validate(new string('a', 256)).IsValid);
    }

    [Fact]
    public void Validate_CountsAstralCharactersAsTwoCodeUnits()
    {
        string astral = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 127));   // 254 code units

        Assert.True(EntryNames.Validate(astral + "a").IsValid);            // 255
        Assert.False(EntryNames.Validate(astral + "ab").IsValid);          // 256
        Assert.Equal(255, (astral + "a").Length);
    }

    [Fact]
    public void Validate_RejectsASplitSurrogatePairAtTheLengthLimit()
    {
        string name = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 127)) + "\uD83D";

        Assert.False(EntryNames.Validate(name).IsValid);
    }

    [Fact]
    public void Validate_OffersASanitisedSuggestion()
    {
        NameCheck check = EntryNames.Validate("bad:name.");

        Assert.False(check.IsValid);
        Assert.Equal("bad_name", check.Suggestion);
    }

    [Fact]
    public void Validate_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EntryNames.Validate(null!));
    }

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("  padded  ", "padded")]
    [InlineData("trailing...", "trailing")]
    [InlineData("trailing. . .", "trailing")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("", "_")]
    [InlineData(".", "_")]
    [InlineData("..", "_")]
    [InlineData("   ", "_")]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("COM4.a.b", "COM4_.a.b")]
    [InlineData("NUL", "NUL_")]
    [InlineData("CONSOLE", "CONSOLE")]
    [InlineData("bell\u0007char", "bellchar")]
    [InlineData("rtl\u202Eoverride.txt", "rtloverride.txt")]
    [InlineData("bom\uFEFF", "bom")]
    [InlineData("e\u0301", "\u00E9")]                                     // NFC composes e + combining acute
    [InlineData("\u0001\u0002\u0003", "_")]
    public void Sanitize_ProducesTheDocumentedOutput(string input, string expected)
    {
        Assert.Equal(expected, EntryNames.Sanitize(input));
    }

    [Fact]
    public void Sanitize_ReplacesAnUnpairedSurrogate()
    {
        Assert.Equal("lone_surrogate", EntryNames.Sanitize("lone\uD800surrogate"));
        Assert.Equal("_", EntryNames.Sanitize("\uD800"));
    }

    [Fact]
    public void Sanitize_TruncatesTo255CodeUnitsAndKeepsTheExtension()
    {
        string sanitized = EntryNames.Sanitize(new string('a', 400) + ".txt");

        Assert.Equal(255, sanitized.Length);
        Assert.EndsWith(".txt", sanitized, StringComparison.Ordinal);
        Assert.Equal(new string('a', 251), sanitized[..251]);
    }

    [Fact]
    public void Sanitize_NeverSplitsASurrogatePairWhenTruncating()
    {
        string sanitized = EntryNames.Sanitize(string.Concat(Enumerable.Repeat("\uD83D\uDE00", 200)));

        Assert.True(sanitized.Length <= 255);
        Assert.False(char.IsHighSurrogate(sanitized[^1]), "A surrogate pair was cut in half.");
        Assert.True(EntryNames.Validate(sanitized).IsValid);
    }

    [Fact]
    public void Sanitize_AlwaysReturnsAValidName()
    {
        string[] nasty =
        [
            "", " ", "...", "..", ".", "CON", "con.txt", "LPT3", "a/b", "\u0000\u0001",
            "\u202Ephp\u202Cexe", "\uFEFF", "\uD800", "\uD800\uDC00", new string('x', 1000),
            new string('.', 300), new string(' ', 300) + "name" + new string(' ', 300),
            "name" + new string('.', 300), "\u00E9\u0301", "nul.nul", "COM1.COM1",
            string.Concat(Enumerable.Repeat("\uD83D\uDE00", 300)),
            new string('a', 300) + "." + new string('b', 300),
        ];

        foreach (string input in nasty)
        {
            string sanitized = EntryNames.Sanitize(input);
            NameCheck check = EntryNames.Validate(sanitized);

            Assert.True(check.IsValid, $"Sanitize(\"{Describe(input)}\") returned \"{Describe(sanitized)}\": {check.Reason}");
            Assert.Equal(sanitized, EntryNames.Sanitize(sanitized));
        }
    }

    [Fact]
    public void Sanitize_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EntryNames.Sanitize(null!));
    }

    [Fact]
    public void MakeUnique_ReturnsTheNameWhenItIsFree()
    {
        Assert.Equal("free.txt", EntryNames.MakeUnique("free.txt", _ => false));
    }

    [Fact]
    public void MakeUnique_CountsUpLikeExplorer()
    {
        var taken = new HashSet<string>(EntryNames.Comparer) { "notes.txt" };
        var produced = new List<string>();

        for (int i = 0; i < 3; i++)
        {
            string next = EntryNames.MakeUnique("notes.txt", taken.Contains);
            produced.Add(next);
            taken.Add(next);
        }

        Assert.Equal(["notes (2).txt", "notes (3).txt", "notes (4).txt"], produced);
    }

    [Theory]
    [InlineData("notes.txt", "notes (2).txt")]
    [InlineData("archive.tar.gz", "archive.tar (2).gz")]
    [InlineData(".gitignore", ".gitignore (2)")]
    [InlineData("folder", "folder (2)")]
    [InlineData("two words.md", "two words (2).md")]
    public void MakeUnique_KeepsTheExtension(string name, string expected)
    {
        Assert.Equal(expected, EntryNames.MakeUnique(name, candidate => EntryNames.Equals(candidate, name)));
    }

    [Fact]
    public void MakeUnique_StaysWithinTheLengthLimit()
    {
        string name = new string('a', 255 - 4) + ".txt";

        string unique = EntryNames.MakeUnique(name, candidate => EntryNames.Equals(candidate, name));

        Assert.True(unique.Length <= 255);
        Assert.EndsWith(" (2).txt", unique, StringComparison.Ordinal);
        Assert.True(EntryNames.Validate(unique).IsValid);
    }

    /// <summary>
    /// The counter-only fallback used to concatenate the suffix and the whole extension with no length
    /// bound at all, so an extension of 252 code units or more produced a name longer than 255. The tree
    /// accepted it and every later save then failed with <c>IndexInvalid</c>.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(251)]
    [InlineData(252)]
    [InlineData(253)]
    [InlineData(300)]
    public void MakeUnique_NeverProducesANameThatFailsValidation(int extensionLength)
    {
        string name = "a." + new string('b', extensionLength);
        string taken = EntryNames.Validate(name).IsValid ? name : EntryNames.Sanitize(name);

        var seen = new HashSet<string>(EntryNames.Comparer) { taken };
        for (int i = 0; i < 4; i++)
        {
            string unique = EntryNames.MakeUnique(taken, seen.Contains);
            NameCheck check = EntryNames.Validate(unique);

            Assert.True(check.IsValid, $"\"{Describe(unique)}\" ({unique.Length} code units): {check.Reason}");
            Assert.True(seen.Add(unique), "the uniquifier returned a name it had already produced");
        }
    }

    /// <summary>
    /// U+2028 and U+2029 are neither C0/C1 nor bidi controls, but WPF and Explorer both break a line on
    /// them even with wrapping off, so "invoice.pdf&lt;U+2028&gt;payload.exe" shows as "invoice.pdf".
    /// </summary>
    [Theory]
    [InlineData("invoice.pdf\u2028payload.exe")]
    [InlineData("invoice.pdf\u2029payload.exe")]
    [InlineData("arabic\u061Cmark.txt")]
    [InlineData("zero\u200Bwidth.txt")]
    [InlineData("joined\u200Dname.txt")]
    [InlineData("soft\u00ADhyphen.txt")]
    [InlineData("word\u2060joiner.txt")]
    public void Validate_RejectsEveryInvisibleFormattingCharacter(string name)
    {
        NameCheck check = EntryNames.Validate(name);

        Assert.False(check.IsValid, $"\"{Describe(name)}\" was accepted.");
        Assert.NotNull(check.Suggestion);
        Assert.True(EntryNames.Validate(check.Suggestion!).IsValid);
        Assert.DoesNotContain(check.Suggestion!, c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format);
    }

    [Fact]
    public void Sanitize_StripsTheSameInvisibleCharactersValidateRejects()
    {
        Assert.Equal("invoice.pdfpayload.exe", EntryNames.Sanitize("invoice.pdf\u2028payload.exe"));
        Assert.Equal("zerowidth", EntryNames.Sanitize("zero\u200Bwidth"));
    }

    [Fact]
    public void MakeUnique_IsCaseInsensitiveWhenTheCallerIs()
    {
        var taken = new HashSet<string>(EntryNames.Comparer) { "NOTES.TXT" };

        Assert.Equal("notes (2).txt", EntryNames.MakeUnique("notes.txt", taken.Contains));
    }

    [Fact]
    public void MakeUnique_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => EntryNames.MakeUnique(null!, _ => false));
        Assert.Throws<ArgumentNullException>(() => EntryNames.MakeUnique("a", null!));
    }

    [Fact]
    public void EqualsAndComparer_IgnoreCase()
    {
        Assert.True(EntryNames.Equals("Notes.TXT", "notes.txt"));
        Assert.False(EntryNames.Equals("notes.txt", "notes2.txt"));
        Assert.Equal(StringComparer.OrdinalIgnoreCase, EntryNames.Comparer);
        Assert.True(EntryNames.Comparer.Equals("A", "a"));
    }

    /// <summary>Renders a string with escapes so a failure message stays readable.</summary>
    /// <param name="value">String to render.</param>
    private static string Describe(string value)
    {
        var builder = new StringBuilder();
        foreach (char c in value)
        {
            builder.Append(c is >= ' ' and <= '~' ? c.ToString() : $"\\u{(int)c:X4}");
        }

        return builder.ToString();
    }
}
