using System.Text;

namespace BastionVault.Core.Format;

/// <summary>
/// In-vault paths (FORMAT.md section 6.3): separator-delimited, rooted, no drive letter, resolved
/// case-insensitively. The UI never concatenates path strings itself.
/// </summary>
public static class VaultPath
{
    /// <summary>The path separator.</summary>
    public const char Separator = '\\';

    /// <summary>The path of the vault root.</summary>
    private const string Root = "\\";

    /// <summary>Joins segments into an in-vault path; returns a single separator when there are none.</summary>
    /// <param name="segments">Path segments from the top level down.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    public static string Format(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var builder = new StringBuilder();
        foreach (string segment in segments)
        {
            builder.Append(Separator).Append(segment);
        }

        return builder.Length == 0 ? Root : builder.ToString();
    }

    /// <summary>
    /// Splits an in-vault path into its segments, rejecting segments that are not valid names. A leading
    /// separator and one trailing separator are both optional (FORMAT.md section 6.3).
    /// </summary>
    /// <param name="vaultPath">Path to split, for example <c>\Documents\2026\notes.txt</c>.</param>
    /// <param name="segments">The segments, or an empty array when the path is the root or invalid.</param>
    /// <returns>True when the path is well formed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vaultPath"/> is <see langword="null"/>.</exception>
    public static bool TrySplit(string vaultPath, out string[] segments)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);

        segments = [];

        // The leading separator is optional; "\" and "" both address the root.
        string body = vaultPath.Length > 0 && vaultPath[0] == Separator ? vaultPath[1..] : vaultPath;

        // A single trailing separator names the same folder, the way "\Docs\" and "\Docs" do in the
        // address bar. Anything beyond that still leaves an empty segment and is rejected below.
        if (body.Length > 0 && body[^1] == Separator)
        {
            body = body[..^1];
        }

        if (body.Length == 0)
        {
            return true;
        }

        string[] parts = body.Split(Separator);
        foreach (string part in parts)
        {
            if (!EntryNames.Validate(part).IsValid)
            {
                return false;
            }
        }

        segments = parts;
        return true;
    }
}
