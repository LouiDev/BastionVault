using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>One file found by a walk, with the in-vault path of its folder already formatted.</summary>
/// <param name="Info">The entry.</param>
/// <param name="FolderPath">In-vault path of the containing folder.</param>
/// <param name="Depth">How many folders deep the file is; a file in the root is at depth 0.</param>
public sealed record WalkedFile(EntryInfo Info, string FolderPath, int Depth);

/// <summary>How many files of one family a vault holds and how much they weigh.</summary>
/// <param name="Group">The family.</param>
/// <param name="Count">Number of files.</param>
/// <param name="Bytes">Sum of their plaintext lengths.</param>
public sealed record GroupTotal(FileTypeGroup Group, int Count, long Bytes);

/// <summary>One of the largest files in a vault.</summary>
/// <param name="Path">In-vault path.</param>
/// <param name="Length">Plaintext length.</param>
public sealed record LargeFile(string Path, long Length);

/// <summary>
/// What a walk of the whole tree can say about a vault that the index statistics cannot: files by
/// family, the largest files, empty folders and how deep the tree goes.
/// </summary>
/// <param name="FileCount">Files seen.</param>
/// <param name="FolderCount">Folders seen, the root excluded.</param>
/// <param name="TotalBytes">Sum of every file's plaintext length.</param>
/// <param name="ByGroup">Families with at least one file, heaviest first.</param>
/// <param name="LargestFiles">The largest files, largest first, at most <see cref="VaultTreeWalker.LargestFilesKept"/>.</param>
/// <param name="EmptyFolders">Folders with nothing in them.</param>
/// <param name="MaxDepth">Deepest nesting of any entry; 0 for a vault with only root entries.</param>
public sealed record VaultContentsSummary(
    int FileCount,
    int FolderCount,
    long TotalBytes,
    IReadOnlyList<GroupTotal> ByGroup,
    IReadOnlyList<LargeFile> LargestFiles,
    int EmptyFolders,
    int MaxDepth);

/// <summary>
/// Walks the tree through the session's snapshot reads, which are allowed at any time and never
/// touch the vault file. Everything here is a pure function of the in-memory tree.
/// </summary>
public static class VaultTreeWalker
{
    /// <summary>How many of the largest files the summary keeps.</summary>
    public const int LargestFilesKept = 5;

    /// <summary>Every file under <paramref name="root"/>, depth first, folders in snapshot order.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="root">Folder to start at; <see cref="EntryId.Root"/> for the whole vault.</param>
    /// <param name="ct">Cancellation token, checked once per folder.</param>
    public static IEnumerable<WalkedFile> EnumerateFiles(IVaultSession session, EntryId root, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var pending = new Stack<(EntryId Folder, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (EntryId folder, int depth) = pending.Pop();
            string folderPath = session.FormatPath(folder);

            foreach (EntryInfo child in session.GetChildren(folder))
            {
                if (child.Kind == EntryKind.Folder)
                {
                    pending.Push((child.Id, depth + 1));
                }
                else
                {
                    yield return new WalkedFile(child, folderPath, depth);
                }
            }
        }
    }

    /// <summary>Summarises the whole vault.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="ct">Cancellation token, checked once per folder.</param>
    public static VaultContentsSummary Summarize(IVaultSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var byGroup = new Dictionary<FileTypeGroup, (int Count, long Bytes)>();
        var largest = new List<LargeFile>(LargestFilesKept + 1);
        int files = 0;
        int folders = 0;
        int emptyFolders = 0;
        int maxDepth = 0;
        long totalBytes = 0;

        var pending = new Stack<(EntryId Folder, int Depth)>();
        pending.Push((EntryId.Root, 0));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            (EntryId folder, int depth) = pending.Pop();
            IReadOnlyList<EntryInfo> children = session.GetChildren(folder);
            if (children.Count == 0 && !folder.IsRoot)
            {
                emptyFolders++;
            }

            string? folderPath = null;
            foreach (EntryInfo child in children)
            {
                maxDepth = Math.Max(maxDepth, depth);
                if (child.Kind == EntryKind.Folder)
                {
                    folders++;
                    pending.Push((child.Id, depth + 1));
                    continue;
                }

                files++;
                totalBytes += child.Length;

                FileTypeGroup group = FileTypeCatalog.Describe(child.Name).Group;
                (int count, long bytes) = byGroup.GetValueOrDefault(group);
                byGroup[group] = (count + 1, bytes + child.Length);

                if (largest.Count < LargestFilesKept || child.Length > largest[^1].Length)
                {
                    folderPath ??= session.FormatPath(folder);
                    largest.Add(new LargeFile(Join(folderPath, child.Name), child.Length));
                    largest.Sort((a, b) => b.Length.CompareTo(a.Length));
                    if (largest.Count > LargestFilesKept)
                    {
                        largest.RemoveAt(largest.Count - 1);
                    }
                }
            }
        }

        List<GroupTotal> groups =
        [
            .. byGroup
                .Select(pair => new GroupTotal(pair.Key, pair.Value.Count, pair.Value.Bytes))
                .OrderByDescending(g => g.Bytes)
                .ThenByDescending(g => g.Count),
        ];

        return new VaultContentsSummary(files, folders, totalBytes, groups, largest, emptyFolders, maxDepth);
    }

    /// <summary>Joins a folder path and a name the way the address bar shows them (API.md: "\" for the root, "\Docs\a.txt").</summary>
    /// <param name="folderPath">The folder's in-vault path, "\" for the root.</param>
    /// <param name="name">The entry name.</param>
    public static string Join(string folderPath, string name) =>
        folderPath.EndsWith('\\') ? folderPath + name : folderPath + "\\" + name;
}
