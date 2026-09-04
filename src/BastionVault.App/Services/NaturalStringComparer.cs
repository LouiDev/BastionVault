using System.Collections;
using System.Runtime.InteropServices;

namespace BastionVault.App.Services;

/// <summary>
/// Orders names the way File Explorer does: digit runs compare as numbers, so "file2" sorts
/// before "file10". The comparison is Windows' own <c>StrCmpLogicalW</c>, which keeps Bastion Vault's
/// list in the same order as the shell the user just came from. If the export cannot be reached
/// the comparer falls back to an equivalent managed implementation rather than throwing.
/// </summary>
public sealed partial class NaturalStringComparer : IComparer<string>, IComparer
{
    /// <summary>The shared instance; the comparer is stateless.</summary>
    public static readonly NaturalStringComparer Instance = new();

    private static bool _useNative = true;

    /// <summary>Compares two names in natural order.</summary>
    /// <param name="x">Left name; <see langword="null"/> sorts first.</param>
    /// <param name="y">Right name; <see langword="null"/> sorts first.</param>
    /// <returns>Negative, zero or positive in the usual comparer sense.</returns>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (_useNative)
        {
            try
            {
                return StrCmpLogicalW(x, y);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _useNative = false;
            }
        }

        return CompareManaged(x, y);
    }

    /// <inheritdoc />
    int IComparer.Compare(object? x, object? y) => Compare(x as string, y as string);

    /// <summary>
    /// The managed equivalent of <c>StrCmpLogicalW</c>: runs of digits compare by value with
    /// leading zeros ignored, everything else compares case-insensitively then case-sensitively
    /// so the order is total.
    /// </summary>
    /// <param name="x">Left name.</param>
    /// <param name="y">Right name.</param>
    internal static int CompareManaged(string x, string y)
    {
        int i = 0;
        int j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsDigit(y[j]))
                {
                    j++;
                }

                ReadOnlySpan<char> left = Trim(x.AsSpan(startX, i - startX));
                ReadOnlySpan<char> right = Trim(y.AsSpan(startY, j - startY));

                if (left.Length != right.Length)
                {
                    return left.Length - right.Length;
                }

                int digits = left.SequenceCompareTo(right);
                if (digits != 0)
                {
                    return digits;
                }

                continue;
            }

            int letters = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (letters != 0)
            {
                return letters;
            }

            i++;
            j++;
        }

        int rest = (x.Length - i) - (y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }

    private static ReadOnlySpan<char> Trim(ReadOnlySpan<char> digits)
    {
        int start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        return digits[start..];
    }

    [LibraryImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string x, string y);
}
