using System.Globalization;
using System.Text;

namespace BastionVault.Core.Format;

/// <summary>
/// Entry name rules (FORMAT.md section 6): validation on parse, on every mutation and again on export,
/// plus the deterministic sanitiser used on import.
/// </summary>
public static class EntryNames
{
    /// <summary>The characters a name may never contain.</summary>
    private const string ForbiddenCharacters = @"\/:*?""<>|";

    /// <summary>The replacement for a character that may not appear in a name.</summary>
    private const char Replacement = '_';

    /// <summary>Device names that Windows reserves; compared against the stem, case-insensitively.</summary>
    private static readonly HashSet<string> ReservedStems = CreateReservedStems();

    /// <summary>
    /// Validates a name per section 6.1: 1 .. 255 UTF-16 code units, none of <c>\ / : * ? " &lt; &gt; |</c>,
    /// no C0/C1 control and no Unicode Cf/Zl/Zp formatting character, not <c>.</c> or <c>..</c>, no leading or trailing whitespace,
    /// no trailing dot, and a stem that is not a reserved device name.
    /// </summary>
    /// <param name="name">Candidate name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public static NameCheck Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string? reason = Describe(name);
        if (reason is null)
        {
            return NameCheck.Ok;
        }

        string candidate = Sanitize(name);
        string? suggestion = Describe(candidate) is null && !string.Equals(candidate, name, StringComparison.Ordinal)
            ? candidate
            : null;

        return new NameCheck(false, reason, suggestion);
    }

    /// <summary>
    /// Turns a disk name into a valid entry name by the deterministic steps 1 to 7 of section 6.2
    /// (NFC, strip controls, trim, replace invalid characters, escape device names, non-empty, truncate to 255).
    /// </summary>
    /// <param name="diskName">Name as it appears on disk.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diskName"/> is <see langword="null"/>.</exception>
    public static string Sanitize(string diskName)
    {
        ArgumentNullException.ThrowIfNull(diskName);

        string name = Normalize(diskName);          // 1. NFC
        name = RemoveInvisible(name);               // 2. remove control and bidi characters
        name = TrimEnds(name);                      // 3. trim whitespace and trailing dots
        name = ReplaceInvalid(name);                // 4. replace invalid characters with '_'
        name = EscapeReservedStem(name);            // 5. escape reserved device names
        if (name.Length == 0)
        {
            name = Replacement.ToString();          // 6. never empty
        }

        if (name.Length > VaultLimits.MaxNameCodeUnits)
        {
            // 7. truncate, keeping the extension when it fits; truncation may expose a new trailing
            // dot or a new reserved stem, so the tail rules are applied once more.
            name = Truncate(name);
            name = TrimEnds(name);
            if (name.Length == 0)
            {
                name = Replacement.ToString();
            }

            name = EscapeReservedStemInPlace(name);
        }

        return name;
    }

    /// <summary>Applies Explorer's rule until the name is free: <c>name (2).ext</c>, <c>name (3).ext</c>, and so on.</summary>
    /// <param name="name">Desired name.</param>
    /// <param name="exists">Predicate telling whether a candidate name is already taken.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No free name exists in the numbered sequence.</exception>
    public static string MakeUnique(string name, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(name))
        {
            return name;
        }

        SplitExtension(name, out string stem, out string extension);
        for (int counter = 2; counter < int.MaxValue; counter++)
        {
            string candidate = Combine(stem, counter, extension);
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No free name is left in the numbered sequence.");
    }

    /// <summary>Compares two names the way sibling uniqueness is defined: <see cref="StringComparison.OrdinalIgnoreCase"/>.</summary>
    /// <param name="a">First name.</param>
    /// <param name="b">Second name.</param>
    public static bool Equals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The comparer that defines name identity inside a vault.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>True when the character may not appear in a name (FORMAT.md section 6.1).</summary>
    /// <param name="c">Character to test.</param>
    internal static bool IsForbidden(char c) =>
        c is '\\' or '/' or ':' or '*' or '?' or '\"' or '<' or '>' or '|';

    /// <summary>True for a C0 or C1 control character.</summary>
    /// <param name="c">Character to test.</param>
    internal static bool IsControl(char c) => c <= '\u001F' || c is >= '\u007F' and <= '\u009F';

    /// <summary>
    /// True for an invisible formatting character: Unicode category <c>Cf</c> (the bidi controls, the
    /// byte-order mark, the zero-width characters, the soft hyphen, the word joiner), <c>Zl</c>
    /// (U+2028 LINE SEPARATOR) or <c>Zp</c> (U+2029 PARAGRAPH SEPARATOR). WPF and Explorer both break a
    /// line on Zl and Zp even with wrapping off, so such a name can hide its own extension.
    /// </summary>
    /// <param name="c">Character to test.</param>
    internal static bool IsBidiOrBom(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    /// <summary>Returns the first violated rule of section 6.1, or <see langword="null"/> when the name is valid.</summary>
    /// <param name="name">Candidate name.</param>
    private static string? Describe(string name)
    {
        if (name.Length < VaultLimits.MinNameCodeUnits)
        {
            return "A name must not be empty.";
        }

        if (name.Length > VaultLimits.MaxNameCodeUnits)
        {
            return $"A name must be at most {VaultLimits.MaxNameCodeUnits} characters long (this one is {name.Length}).";
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (IsForbidden(c))
            {
                return $"A name must not contain any of {ForbiddenCharacters} (found '{c}').";
            }

            if (IsControl(c))
            {
                return $"A name must not contain control characters (found U+{(int)c:X4}).";
            }

            if (IsBidiOrBom(c))
            {
                return $"A name must not contain invisible formatting characters (found U+{(int)c:X4}).";
            }

            if (char.IsSurrogate(c) && !IsWellFormedSurrogatePair(name, i))
            {
                return "A name must not contain unpaired surrogate characters.";
            }
        }

        if (name is "." or "..")
        {
            return @"""."" and "".."" are reserved and cannot be used as a name.";
        }

        if (char.IsWhiteSpace(name[0]))
        {
            return "A name must not start with whitespace.";
        }

        if (char.IsWhiteSpace(name[^1]))
        {
            return "A name must not end with whitespace.";
        }

        if (name[^1] == '.')
        {
            return "A name must not end with a dot.";
        }

        string stem = Stem(name);
        if (ReservedStems.Contains(stem))
        {
            return $"\"{stem}\" is a reserved device name and cannot be used, even with an extension.";
        }

        return null;
    }

    /// <summary>True when the surrogate at <paramref name="index"/> is part of a well-formed pair.</summary>
    /// <param name="name">The name being scanned.</param>
    /// <param name="index">Index of the surrogate code unit.</param>
    private static bool IsWellFormedSurrogatePair(string name, int index)
    {
        char c = name[index];
        if (char.IsHighSurrogate(c))
        {
            return index + 1 < name.Length && char.IsLowSurrogate(name[index + 1]);
        }

        return index > 0 && char.IsHighSurrogate(name[index - 1]);
    }

    /// <summary>The reserved-name stem: the text before the first dot, or the whole name.</summary>
    /// <param name="name">Name to split.</param>
    private static string Stem(string name)
    {
        int dot = name.IndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>Step 1: NFC-normalises, falling back to the original when the string cannot be normalised.</summary>
    /// <param name="value">Name to normalise.</param>
    private static string Normalize(string value)
    {
        try
        {
            return value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Unpaired surrogates cannot be normalised; they are replaced in step 4.
            return value;
        }
    }

    /// <summary>Step 2: drops control, bidi and byte-order-mark characters.</summary>
    /// <param name="value">Name to filter.</param>
    private static string RemoveInvisible(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!IsControl(c) && !IsBidiOrBom(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Step 3: trims leading whitespace and trailing whitespace and dots.</summary>
    /// <param name="value">Name to trim.</param>
    private static string TrimEnds(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && (char.IsWhiteSpace(value[end - 1]) || value[end - 1] == '.'))
        {
            end--;
        }

        return value[start..end];
    }

    /// <summary>Step 4: replaces forbidden characters and unpaired surrogates with an underscore.</summary>
    /// <param name="value">Name to clean.</param>
    private static string ReplaceInvalid(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (IsForbidden(c) || (char.IsSurrogate(c) && !IsWellFormedSurrogatePair(value, i)))
            {
                builder.Append(Replacement);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Step 5: appends an underscore to a stem that is a reserved device name.</summary>
    /// <param name="value">Name to check.</param>
    private static string EscapeReservedStem(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        string stem = Stem(value);
        return ReservedStems.Contains(stem) ? string.Concat(stem, Replacement.ToString(), value[stem.Length..]) : value;
    }

    /// <summary>
    /// Escapes a reserved stem without growing the name: the last character of the stem becomes an
    /// underscore. Used after truncation, where appending would exceed the length limit.
    /// </summary>
    /// <param name="value">Name to check.</param>
    private static string EscapeReservedStemInPlace(string value)
    {
        string stem = Stem(value);
        if (stem.Length == 0 || !ReservedStems.Contains(stem))
        {
            return value;
        }

        return string.Concat(value[..(stem.Length - 1)], Replacement.ToString(), value[stem.Length..]);
    }

    /// <summary>Step 7: truncates to 255 code units, keeping the extension when it fits.</summary>
    /// <param name="value">Name longer than the limit.</param>
    private static string Truncate(string value)
    {
        SplitExtension(value, out string head, out string extension);
        if (extension.Length > VaultLimits.MaxNameCodeUnits - 1)
        {
            head = value;
            extension = string.Empty;
        }

        int room = VaultLimits.MaxNameCodeUnits - extension.Length;
        if (head.Length > room)
        {
            head = head[..room];
        }

        if (head.Length > 0 && char.IsHighSurrogate(head[^1]))
        {
            head = head[..^1];
        }

        return string.Concat(head, extension);
    }

    /// <summary>Splits a name into stem and extension; the extension starts at the last dot of a non-empty stem.</summary>
    /// <param name="name">Name to split.</param>
    /// <param name="stem">The part before the extension.</param>
    /// <param name="extension">The extension including its dot, or an empty string.</param>
    private static void SplitExtension(string name, out string stem, out string extension)
    {
        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            stem = name[..dot];
            extension = name[dot..];
        }
        else
        {
            stem = name;
            extension = string.Empty;
        }
    }

    /// <summary>Builds <c>stem (counter).ext</c>, shortening the stem so the result stays within the limit.</summary>
    /// <param name="stem">Stem of the original name.</param>
    /// <param name="counter">Number to append, starting at 2.</param>
    /// <param name="extension">Extension including its dot, or an empty string.</param>
    private static string Combine(string stem, int counter, string extension)
    {
        string suffix = string.Create(CultureInfo.InvariantCulture, $" ({counter})");
        int room = VaultLimits.MaxNameCodeUnits - extension.Length - suffix.Length;
        if (room < 1)
        {
            // Pathological extension length: fall back to a bare counter, shortened to the limit. An
            // unbounded fallback produced a name the index serializer then refused, which blocked every
            // save until the offending entry was renamed (FORMAT.md section 6.1).
            return CounterOnly(suffix, extension);
        }

        string head = stem.Length > room ? stem[..room] : stem;
        if (head.Length > 0 && char.IsHighSurrogate(head[^1]))
        {
            head = head[..^1];
        }

        head = TrimEnds(head);
        return head.Length == 0
            ? CounterOnly(suffix, extension)
            : string.Concat(head, suffix, extension);
    }

    /// <summary>
    /// The counter-only form of a unique name, <c>(2).ext</c>, with the extension shortened so the whole
    /// name stays within <see cref="VaultLimits.MaxNameCodeUnits"/> and still passes validation.
    /// </summary>
    /// <param name="suffix">The <c> (counter)</c> suffix, still carrying its leading space.</param>
    /// <param name="extension">Extension including its dot, or an empty string.</param>
    private static string CounterOnly(string suffix, string extension)
    {
        string counter = suffix.TrimStart();
        int room = VaultLimits.MaxNameCodeUnits - counter.Length;
        string tail = room <= 0 ? string.Empty : extension.Length > room ? extension[..room] : extension;
        if (tail.Length > 0 && char.IsHighSurrogate(tail[^1]))
        {
            tail = tail[..^1];
        }

        string candidate = string.Concat(counter, tail);
        return Describe(candidate) is null ? candidate : counter;
    }

    /// <summary>Builds the set of reserved device stems: CON, PRN, AUX, NUL, COM0-9 and LPT0-9.</summary>
    private static HashSet<string> CreateReservedStems()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int i = 0; i <= 9; i++)
        {
            set.Add(string.Create(CultureInfo.InvariantCulture, $"COM{i}"));
            set.Add(string.Create(CultureInfo.InvariantCulture, $"LPT{i}"));
        }

        return set;
    }
}
