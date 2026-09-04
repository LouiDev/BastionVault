using Microsoft.Win32.SafeHandles;

namespace Bastion.Core.Session;

/// <summary>
/// Removes the artefacts a crashed session may leave behind (FORMAT.md section 8.5): staging containers
/// and save temporaries. A live session holds its own files exclusively, so they are simply skipped.
/// </summary>
internal static class OrphanSweeper
{
    /// <summary>
    /// The two name shapes a session ever creates next to a vault. Neither may assume the
    /// <c>.bastion</c> extension: the save temporary is named after whatever the vault file is called,
    /// so a vault named <c>archive.vault</c> leaves <c>archive.vault.tmp-1a2b3c4d</c> behind. Deletion
    /// is still safe because every candidate must first yield an exclusive delete-on-close handle.
    /// </summary>
    private static readonly string[] Patterns = ["*~stage-*", "*.tmp-*"];

    /// <summary>Sweeps a set of directories.</summary>
    /// <param name="directories">Directories to sweep.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes reclaimed.</returns>
    public static long Sweep(IEnumerable<string> directories, CancellationToken ct)
    {
        long reclaimed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(directory) || !seen.Add(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (string pattern in Patterns)
            {
                foreach (string path in Enumerate(directory, pattern))
                {
                    ct.ThrowIfCancellationRequested();
                    reclaimed += TryReclaim(path);
                }
            }
        }

        return reclaimed;
    }

    /// <summary>Lists candidates, ignoring a directory that cannot be read.</summary>
    /// <param name="directory">Directory to list.</param>
    /// <param name="pattern">Name pattern.</param>
    private static string[] Enumerate(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes one orphan if an exclusive handle on it can be taken.</summary>
    /// <param name="path">File to reclaim.</param>
    /// <returns>The bytes reclaimed, or 0 when the file is in use.</returns>
    private static long TryReclaim(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            return RandomAccess.GetLength(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A live session holds its container and its temporary file open; leave them alone.
            return 0;
        }
    }
}
