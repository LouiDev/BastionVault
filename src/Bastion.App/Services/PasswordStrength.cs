using System.Globalization;
using System.Security.Cryptography;
using Bastion.Core;

namespace Bastion.App.Services;

/// <summary>How strong a password is, as a coarse band for the meter.</summary>
public enum PasswordStrengthLevel
{
    /// <summary>Guessed immediately: a dictionary word or a pure pattern.</summary>
    VeryWeak,

    /// <summary>Guessed quickly even against a slow KDF.</summary>
    Weak,

    /// <summary>Survives casual guessing but not a funded attacker.</summary>
    Fair,

    /// <summary>Survives a funded attacker at the configured KDF cost.</summary>
    Strong,

    /// <summary>Out of reach of any offline attack.</summary>
    VeryStrong,
}

/// <summary>What kind of structure the estimator recognised in a password.</summary>
public enum PatternKind
{
    /// <summary>No structure; charged at full character-set entropy.</summary>
    BruteForce,

    /// <summary>A common password or a common password with a predictable suffix.</summary>
    Dictionary,

    /// <summary>The same character or group repeated.</summary>
    Repeat,

    /// <summary>An ascending or descending run such as "abcd" or "9876".</summary>
    Sequence,

    /// <summary>A walk across the keyboard such as "qwerty" or "1qaz".</summary>
    KeyboardWalk,

    /// <summary>A year or a full date.</summary>
    Date,
}

/// <summary>One recognised piece of a password.</summary>
/// <param name="Kind">What was recognised.</param>
/// <param name="Start">Index of the first character of the match.</param>
/// <param name="Length">Number of characters the match covers.</param>
/// <param name="Entropy">Bits the match contributes.</param>
public readonly record struct PasswordPattern(PatternKind Kind, int Start, int Length, double Entropy);

/// <summary>The estimator's verdict.</summary>
/// <param name="Length">Length of the password in characters.</param>
/// <param name="Entropy">Estimated bits of entropy.</param>
/// <param name="Level">Coarse band for the meter.</param>
/// <param name="Patterns">Every recognised piece, left to right.</param>
/// <param name="Weakness">The single most useful thing to tell the user, or <see langword="null"/>.</param>
public sealed record PasswordStrengthResult(
    int Length,
    double Entropy,
    PasswordStrengthLevel Level,
    IReadOnlyList<PasswordPattern> Patterns,
    string? Weakness)
{
    /// <summary>The verdict for an empty password.</summary>
    public static readonly PasswordStrengthResult Empty =
        new(0, 0, PasswordStrengthLevel.VeryWeak, [], null);
}

/// <summary>
/// A zxcvbn-shaped strength estimator that never materialises the password as a
/// <see cref="string"/> (UI-CONTRACT.md section 1.3): it works over a span, writes its two
/// normalised forms into pinned scratch buffers and zeroes them before it returns.
///
/// The score is a sum over recognised pieces - common-password hits (charged log2 of their rank),
/// repeats, sequences, keyboard walks and dates - with everything unrecognised charged at the
/// full character-set rate. The crack-time sentence then converts those bits into wall-clock time
/// at the vault's own Argon2id cost, which is the only number a user can act on.
/// </summary>
public static class PasswordStrength
{
    /// <summary>Hard minimum length enforced by the New vault dialog.</summary>
    public const int MinimumLength = 8;

    /// <summary>Number of GPUs the crack-time sentence assumes.</summary>
    private const double GpuCount = 8;

    /// <summary>Guesses per second one GPU manages at 1 MiB and one pass.</summary>
    private const double GuessesPerSecondAtUnitCost = 1e9;

    private static readonly string[] KeyboardRows =
    [
        "`1234567890-=",
        "qwertyuiop[]\\",
        "asdfghjkl;'",
        "zxcvbnm,./",
    ];

    private static readonly double[] RowOffsets = [0.0, 1.0, 1.5, 2.0];

    /// <summary>Estimates the strength of a password.</summary>
    /// <param name="password">The password characters; never copied into a managed string.</param>
    public static PasswordStrengthResult Estimate(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
        {
            return PasswordStrengthResult.Empty;
        }

        int length = password.Length;
        char[] lower = GC.AllocateArray<char>(length, pinned: true);
        char[] leet = GC.AllocateArray<char>(length, pinned: true);

        try
        {
            for (int i = 0; i < length; i++)
            {
                char c = char.ToLowerInvariant(password[i]);
                lower[i] = c;
                leet[i] = Unleet(c);
            }

            double classEntropy = Math.Log2(CharsetSize(password));
            var patterns = new List<PasswordPattern>();
            double total = 0;
            int index = 0;

            while (index < length)
            {
                PasswordPattern? match = BestMatch(password, lower, leet, index, classEntropy);
                if (match is { } found)
                {
                    patterns.Add(found);
                    total += found.Entropy;
                    index += found.Length;
                }
                else
                {
                    patterns.Add(new PasswordPattern(PatternKind.BruteForce, index, 1, classEntropy));
                    total += classEntropy;
                    index++;
                }
            }

            // A password made of a single recognised piece has no combination entropy at all.
            if (patterns.Count > 1)
            {
                total += Math.Log2(patterns.Count);
            }

            PasswordStrengthLevel level = LevelFor(total, length);
            return new PasswordStrengthResult(length, total, level, patterns, WeaknessFor(patterns, length));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(lower.AsSpan()));
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(leet.AsSpan()));
        }
    }

    /// <summary>
    /// Guesses per second the sentence assumes: eight high-end GPUs, each managing
    /// 1e9 / (memory in MiB x passes) Argon2id evaluations per second.
    /// </summary>
    /// <param name="kdf">The vault's Argon2id parameters.</param>
    public static double GuessesPerSecond(KdfParameters kdf)
    {
        ArgumentNullException.ThrowIfNull(kdf);

        double mebibytes = Math.Max(1, kdf.MemoryKiB / 1024.0);
        double perGpu = GuessesPerSecondAtUnitCost / (mebibytes * Math.Max(1, kdf.Iterations));
        return GpuCount * perGpu;
    }

    /// <summary>Seconds an offline attacker needs at <paramref name="kdf"/>, given the estimated bits.</summary>
    /// <param name="entropyBits">Estimated entropy.</param>
    /// <param name="kdf">The vault's Argon2id parameters.</param>
    public static double CrackSeconds(double entropyBits, KdfParameters kdf) =>
        Math.Pow(2, entropyBits) / GuessesPerSecond(kdf);

    /// <summary>
    /// The sentence shown under the password field, for example
    /// "At Standard, eight high-end GPUs would need about 4 thousand years to guess this password."
    /// </summary>
    /// <param name="result">The estimator's verdict.</param>
    /// <param name="kdf">The vault's Argon2id parameters.</param>
    /// <param name="presetName">Name of the preset, as shown in the preset radio group.</param>
    public static string Sentence(PasswordStrengthResult result, KdfParameters kdf, string presetName)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Length == 0)
        {
            return "Type a password to see how long it would take to guess.";
        }

        string duration = FormatDuration(CrackSeconds(result.Entropy, kdf));

        // "about" only reads as English in front of a bare quantity. Both open-ended phrases the
        // formatter can return are already complete comparisons, and "about longer than the age
        // of the universe" was the sentence a strong password produced.
        bool isPhrase =
            duration.StartsWith("less than", StringComparison.Ordinal)
            || duration.StartsWith("longer than", StringComparison.Ordinal);
        string lead = isPhrase ? string.Empty : "about ";
        return $"At {presetName}, eight high-end GPUs would need {lead}{duration} to guess this password.";
    }

    /// <summary>Renders a number of seconds as the coarse phrase the sentence uses.</summary>
    /// <param name="seconds">Seconds; may be infinite.</param>
    public static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 1)
        {
            return "less than a second";
        }

        if (double.IsInfinity(seconds))
        {
            return "longer than the age of the universe";
        }

        const double Minute = 60;
        const double Hour = 60 * Minute;
        const double Day = 24 * Hour;
        const double Month = 30 * Day;
        const double Year = 365.25 * Day;

        if (seconds < Minute)
        {
            return Plural(seconds, "second");
        }

        if (seconds < Hour)
        {
            return Plural(seconds / Minute, "minute");
        }

        if (seconds < Day)
        {
            return Plural(seconds / Hour, "hour");
        }

        if (seconds < Month)
        {
            return Plural(seconds / Day, "day");
        }

        if (seconds < Year)
        {
            return Plural(seconds / Month, "month");
        }

        double years = seconds / Year;
        if (years < 1_000)
        {
            return Plural(years, "year");
        }

        if (years < 1e6)
        {
            return $"{Round(years / 1e3)} thousand years";
        }

        if (years < 1e9)
        {
            return $"{Round(years / 1e6)} million years";
        }

        if (years < 1.4e10)
        {
            return $"{Round(years / 1e9)} billion years";
        }

        return "longer than the age of the universe";
    }

    private static string Plural(double value, string unit)
    {
        long rounded = (long)Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero));
        return rounded == 1
            ? $"1 {unit}"
            : $"{rounded.ToString("N0", CultureInfo.CurrentCulture)} {unit}s";
    }

    private static string Round(double value)
    {
        long rounded = (long)Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero));
        return rounded.ToString("N0", CultureInfo.CurrentCulture);
    }

    private static PasswordStrengthLevel LevelFor(double entropy, int length) => entropy switch
    {
        < 28 => PasswordStrengthLevel.VeryWeak,
        < 40 => PasswordStrengthLevel.Weak,
        < 56 => length < MinimumLength ? PasswordStrengthLevel.Weak : PasswordStrengthLevel.Fair,
        < 76 => PasswordStrengthLevel.Strong,
        _ => PasswordStrengthLevel.VeryStrong,
    };

    private static string? WeaknessFor(IReadOnlyList<PasswordPattern> patterns, int length)
    {
        PasswordPattern worst = default;
        double coverage = 0;

        foreach (PasswordPattern pattern in patterns)
        {
            if (pattern.Kind == PatternKind.BruteForce)
            {
                continue;
            }

            if (pattern.Length > worst.Length)
            {
                worst = pattern;
            }

            coverage += pattern.Length;
        }

        if (worst.Length == 0)
        {
            return length < MinimumLength ? "Too short: use at least eight characters." : null;
        }

        bool dominant = coverage >= length * 0.6;
        return worst.Kind switch
        {
            PatternKind.Dictionary when dominant => "This is a common password, or a common password with something tacked on.",
            PatternKind.Dictionary => "Part of this is a common password.",
            PatternKind.Repeat => "Repeated characters add almost nothing.",
            PatternKind.Sequence => "Runs like \"abcd\" and \"1234\" are guessed first.",
            PatternKind.KeyboardWalk => "Walks across the keyboard are guessed first.",
            PatternKind.Date => "Dates and years are guessed first.",
            _ => null,
        };
    }

    private static PasswordPattern? BestMatch(
        ReadOnlySpan<char> password, char[] lower, char[] leet, int index, double classEntropy)
    {
        PasswordPattern? best = null;

        void Consider(PasswordPattern candidate)
        {
            if (best is not { } current
                || candidate.Length > current.Length
                || (candidate.Length == current.Length && candidate.Entropy < current.Entropy))
            {
                best = candidate;
            }
        }

        if (MatchDictionary(password, lower, leet, index) is { } dictionary)
        {
            Consider(dictionary);
        }

        if (MatchRepeat(lower, index, classEntropy) is { } repeat)
        {
            Consider(repeat);
        }

        if (MatchSequence(lower, index) is { } sequence)
        {
            Consider(sequence);
        }

        if (MatchKeyboard(lower, index) is { } keyboard)
        {
            Consider(keyboard);
        }

        if (MatchDate(lower, index) is { } date)
        {
            Consider(date);
        }

        return best;
    }

    private static PasswordPattern? MatchDictionary(
        ReadOnlySpan<char> password, char[] lower, char[] leet, int index)
    {
        int remaining = lower.Length - index;
        int maxLength = Math.Min(remaining, 32);

        for (int len = maxLength; len >= 3; len--)
        {
            var plain = new ReadOnlySpan<char>(lower, index, len);
            int? rank = LookupRank(plain);
            bool leetUsed = false;

            if (rank is null)
            {
                var unleeted = new ReadOnlySpan<char>(leet, index, len);
                if (!unleeted.SequenceEqual(plain))
                {
                    rank = LookupRank(unleeted);
                    leetUsed = rank is not null;
                }
            }

            if (rank is null)
            {
                continue;
            }

            double entropy = Math.Log2(Math.Max(1, rank.Value));
            entropy += CasingBits(password.Slice(index, len));
            if (leetUsed)
            {
                entropy += 1.5;
            }

            return new PasswordPattern(PatternKind.Dictionary, index, len, Math.Max(1, entropy));
        }

        return null;
    }

    private static int? LookupRank(ReadOnlySpan<char> candidate) => CommonPasswords.Rank(candidate);

    private static double CasingBits(ReadOnlySpan<char> token)
    {
        int upper = 0;
        int letters = 0;
        foreach (char c in token)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (char.IsUpper(c))
                {
                    upper++;
                }
            }
        }

        if (letters == 0 || upper == 0 || upper == letters)
        {
            return 0;
        }

        // Only the first letter capitalised is the overwhelmingly common case and buys one bit.
        return char.IsUpper(token[0]) && upper == 1 ? 1 : Math.Log2(letters);
    }

    private static PasswordPattern? MatchRepeat(char[] lower, int index, double classEntropy)
    {
        int end = index + 1;
        while (end < lower.Length && lower[end] == lower[index])
        {
            end++;
        }

        int length = end - index;
        if (length < 3)
        {
            return null;
        }

        double entropy = classEntropy + Math.Log2(length);
        return new PasswordPattern(PatternKind.Repeat, index, length, entropy);
    }

    private static PasswordPattern? MatchSequence(char[] lower, int index)
    {
        if (index + 2 >= lower.Length)
        {
            return null;
        }

        int delta = lower[index + 1] - lower[index];
        if (delta is not (1 or -1))
        {
            return null;
        }

        int end = index + 1;
        while (end + 1 < lower.Length && lower[end + 1] - lower[end] == delta)
        {
            end++;
        }

        int length = end - index + 1;
        if (length < 3)
        {
            return null;
        }

        // A run is fixed by its first character, its direction and its length.
        double entropy = Math.Log2(36) + 1 + Math.Log2(length);
        return new PasswordPattern(PatternKind.Sequence, index, length, entropy);
    }

    private static PasswordPattern? MatchKeyboard(char[] lower, int index)
    {
        int end = index;
        while (end + 1 < lower.Length && AreAdjacent(lower[end], lower[end + 1]))
        {
            end++;
        }

        int length = end - index + 1;
        if (length < 4)
        {
            return null;
        }

        double entropy = Math.Log2(47) + Math.Log2(length) + Math.Log2(6) * (length - 1) / 4;
        return new PasswordPattern(PatternKind.KeyboardWalk, index, length, entropy);
    }

    private static PasswordPattern? MatchDate(char[] lower, int index)
    {
        int remaining = lower.Length - index;

        if (remaining >= 4 && IsDigits(lower, index, 4))
        {
            int year = Number(lower, index, 4);
            if (year is >= 1900 and <= 2099)
            {
                // Roughly 200 plausible years.
                return new PasswordPattern(PatternKind.Date, index, 4, Math.Log2(200));
            }
        }

        if (remaining >= 6 && IsDigits(lower, index, 6))
        {
            // ddmmyy / mmddyy: about 37 000 plausible dates.
            return new PasswordPattern(PatternKind.Date, index, 6, Math.Log2(37_000));
        }

        return null;
    }

    private static bool IsDigits(char[] buffer, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (!char.IsAsciiDigit(buffer[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int Number(char[] buffer, int start, int count)
    {
        int value = 0;
        for (int i = start; i < start + count; i++)
        {
            value = (value * 10) + (buffer[i] - '0');
        }

        return value;
    }

    private static bool AreAdjacent(char a, char b)
    {
        if (a == b)
        {
            return false;
        }

        if (!TryLocate(a, out int rowA, out double xA) || !TryLocate(b, out int rowB, out double xB))
        {
            return false;
        }

        int rowDelta = Math.Abs(rowA - rowB);
        double xDelta = Math.Abs(xA - xB);

        return rowDelta switch
        {
            0 => Math.Abs(xDelta - 1) < 0.01,
            1 => xDelta <= 1.0,
            _ => false,
        };
    }

    private static bool TryLocate(char c, out int row, out double x)
    {
        for (int r = 0; r < KeyboardRows.Length; r++)
        {
            int column = KeyboardRows[r].IndexOf(c, StringComparison.Ordinal);
            if (column >= 0)
            {
                row = r;
                x = RowOffsets[r] + column;
                return true;
            }
        }

        row = -1;
        x = 0;
        return false;
    }

    private static int CharsetSize(ReadOnlySpan<char> password)
    {
        bool lower = false;
        bool upper = false;
        bool digit = false;
        bool symbol = false;
        bool other = false;

        foreach (char c in password)
        {
            if (char.IsAsciiLetterLower(c))
            {
                lower = true;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                upper = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                digit = true;
            }
            else if (c < 128)
            {
                symbol = true;
            }
            else
            {
                other = true;
            }
        }

        int size = 0;
        if (lower)
        {
            size += 26;
        }

        if (upper)
        {
            size += 26;
        }

        if (digit)
        {
            size += 10;
        }

        if (symbol)
        {
            size += 33;
        }

        if (other)
        {
            size += 100;
        }

        return Math.Max(size, 2);
    }

    private static char Unleet(char c) => c switch
    {
        '4' or '@' => 'a',
        '3' => 'e',
        '1' or '!' or '|' => 'i',
        '0' => 'o',
        '5' or '$' => 's',
        '7' => 't',
        '8' => 'b',
        '9' => 'g',
        '+' => 't',
        _ => c,
    };
}
