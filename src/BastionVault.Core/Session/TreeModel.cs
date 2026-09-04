using BastionVault.Core.Format;

namespace BastionVault.Core.Session;

/// <summary>One node of the in-memory tree. Mutable and internal; the session only hands out snapshots.</summary>
internal sealed class TreeNode
{
    /// <summary>Stable entry id; 0 for the implicit root.</summary>
    public uint Id { get; init; }

    /// <summary>Folder or file.</summary>
    public EntryKind Kind { get; init; }

    /// <summary>Entry name, valid per FORMAT.md section 6.1. Empty for the root.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-text comment.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Creation time in <see cref="DateTime"/> ticks (UTC).</summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>Modification time in <see cref="DateTime"/> ticks (UTC).</summary>
    public long ModifiedUtcTicks { get; set; }

    /// <summary>State relative to the last successful save.</summary>
    public EntryState State { get; set; }

    /// <summary>Content of a file entry; <see langword="null"/> for folders.</summary>
    public BlobRef? Content { get; set; }

    /// <summary>The containing folder, or <see langword="null"/> while the node is detached.</summary>
    public TreeNode? Parent { get; set; }

    /// <summary>Direct children, in insertion order; ordering for the UI happens in <see cref="TreeModel"/>.</summary>
    public List<TreeNode> Children { get; } = [];

    /// <summary>Cached recursive plaintext size of a folder.</summary>
    public long RollupBytes { get; set; }

    /// <summary>False while <see cref="RollupBytes"/> must be recomputed.</summary>
    public bool RollupValid { get; set; }

    /// <summary>Id of the containing folder; 0 when the node sits at the top level.</summary>
    public uint ParentId => Parent?.Id ?? 0;
}

/// <summary>
/// The in-memory tree: nodes by id, canonical orderings, cached folder rollups, path formatting and
/// resolution, name validation and search. It holds no I/O and no cryptography.
/// </summary>
internal sealed class TreeModel
{
    private readonly Dictionary<uint, TreeNode> _byId = [];

    /// <summary>Creates an empty tree with only the implicit root.</summary>
    public TreeModel()
    {
        Root = new TreeNode { Id = 0, Kind = EntryKind.Folder };
        _byId[0] = Root;
        NextEntryId = 1;
    }

    /// <summary>The implicit root folder (id 0). It has no <see cref="EntryInfo"/>.</summary>
    public TreeNode Root { get; }

    /// <summary>Next id to allocate; greater than every id in the tree.</summary>
    public uint NextEntryId { get; set; }

    /// <summary>Number of folders, excluding the root.</summary>
    public int FolderCount { get; private set; }

    /// <summary>Number of files.</summary>
    public int FileCount { get; private set; }

    /// <summary>Sum of all file plaintext lengths.</summary>
    public long TotalPlaintextBytes { get; private set; }

    /// <summary>Allocates the next entry id.</summary>
    /// <exception cref="VaultOperationException">The id space is exhausted.</exception>
    public uint AllocateId()
    {
        if (NextEntryId >= uint.MaxValue)
        {
            throw new VaultOperationException(
                VaultErrorCode.NameConflict,
                "This vault has used every entry id the format allows; create a new vault.");
        }

        return NextEntryId++;
    }

    /// <summary>Looks up a node by id; the root answers to id 0.</summary>
    /// <param name="id">Entry id.</param>
    public TreeNode? Find(uint id) => _byId.TryGetValue(id, out TreeNode? node) ? node : null;

    /// <summary>Looks up a node by id.</summary>
    /// <param name="id">Entry id.</param>
    public TreeNode? Find(EntryId id) => Find(id.Value);

    /// <summary>Attaches a detached node (with its whole subtree) to a folder.</summary>
    /// <param name="node">Node to attach.</param>
    /// <param name="parent">Destination folder.</param>
    public void Attach(TreeNode node, TreeNode parent)
    {
        node.Parent = parent;
        parent.Children.Add(node);
        foreach (TreeNode member in Subtree(node))
        {
            _byId[member.Id] = member;
            Count(member, +1);
        }

        InvalidateRollups(parent);
    }

    /// <summary>Detaches a node and its whole subtree from the tree.</summary>
    /// <param name="node">Node to detach.</param>
    public void Detach(TreeNode node)
    {
        TreeNode? parent = node.Parent;
        parent?.Children.Remove(node);
        node.Parent = null;
        foreach (TreeNode member in Subtree(node))
        {
            _byId.Remove(member.Id);
            Count(member, -1);
        }

        if (parent is not null)
        {
            InvalidateRollups(parent);
        }
    }

    /// <summary>Enumerates a node and all its descendants, parents before children.</summary>
    /// <param name="node">Root of the walk.</param>
    public static IEnumerable<TreeNode> Subtree(TreeNode node)
    {
        var stack = new Stack<TreeNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            TreeNode current = stack.Pop();
            yield return current;
            for (int i = current.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(current.Children[i]);
            }
        }
    }

    /// <summary>
    /// Enumerates every entry in the canonical order of FORMAT.md section 4.5: depth-first pre-order
    /// from the root, children by ascending id. The root itself is not an entry and is skipped.
    /// </summary>
    public IEnumerable<TreeNode> CanonicalOrder()
    {
        var stack = new Stack<TreeNode>();
        PushByDescendingId(stack, Root);
        while (stack.Count > 0)
        {
            TreeNode current = stack.Pop();
            yield return current;
            PushByDescendingId(stack, current);
        }
    }

    /// <summary>Children of a folder for the UI: folders first, then files, each in natural name order.</summary>
    /// <param name="folder">Folder to list.</param>
    public static List<TreeNode> OrderedChildren(TreeNode folder)
    {
        var children = new List<TreeNode>(folder.Children);
        children.Sort(static (a, b) =>
        {
            if (a.Kind != b.Kind)
            {
                return a.Kind == EntryKind.Folder ? -1 : 1;
            }

            return NaturalNameComparer.Instance.Compare(a.Name, b.Name);
        });

        return children;
    }

    /// <summary>Returns the child of a folder with that name, compared case-insensitively.</summary>
    /// <param name="folder">Folder to search.</param>
    /// <param name="name">Name to look for.</param>
    public static TreeNode? FindChild(TreeNode folder, string name)
    {
        foreach (TreeNode child in folder.Children)
        {
            if (EntryNames.Equals(child.Name, name))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Depth of a node with the root counting as 0.</summary>
    /// <param name="node">Node to measure.</param>
    public static int DepthOf(TreeNode node)
    {
        int depth = 0;
        for (TreeNode? current = node; current is not null && current.Id != 0; current = current.Parent)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>Height of a subtree: 0 for a leaf.</summary>
    /// <param name="node">Root of the subtree.</param>
    public static int HeightOf(TreeNode node)
    {
        int baseDepth = DepthOf(node);
        int height = 0;
        foreach (TreeNode member in Subtree(node))
        {
            height = Math.Max(height, DepthOf(member) - baseDepth);
        }

        return height;
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or sits below it.</summary>
    /// <param name="node">Candidate descendant.</param>
    /// <param name="ancestor">Candidate ancestor.</param>
    public static bool IsSelfOrDescendantOf(TreeNode node, TreeNode ancestor)
    {
        for (TreeNode? current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats the in-vault path of a node.</summary>
    /// <param name="node">Node to format.</param>
    public static string FormatPath(TreeNode node)
    {
        var segments = new List<string>();
        for (TreeNode? current = node; current is not null && current.Id != 0; current = current.Parent)
        {
            segments.Add(current.Name);
        }

        segments.Reverse();
        return VaultPath.Format(segments);
    }

    /// <summary>Resolves an in-vault path case-insensitively.</summary>
    /// <param name="vaultPath">Path to resolve.</param>
    /// <param name="node">The node found, or the root.</param>
    /// <returns>True when the path resolves.</returns>
    public bool TryResolve(string vaultPath, out TreeNode node)
    {
        node = Root;
        if (!VaultPath.TrySplit(vaultPath, out string[] segments))
        {
            return false;
        }

        TreeNode current = Root;
        foreach (string segment in segments)
        {
            TreeNode? child = FindChild(current, segment);
            if (child is null)
            {
                return false;
            }

            current = child;
        }

        node = current;
        return true;
    }

    /// <summary>Checks a name against FORMAT.md section 6.1 and against the siblings of a folder.</summary>
    /// <param name="parent">Folder the name would live in.</param>
    /// <param name="name">Candidate name.</param>
    /// <param name="ignoring">Entry to ignore during the uniqueness check.</param>
    public static NameCheck ValidateName(TreeNode parent, string name, uint? ignoring)
    {
        NameCheck check = EntryNames.Validate(name);
        if (!check.IsValid)
        {
            string sanitized = EntryNames.Sanitize(name);
            string suggestion = EntryNames.MakeUnique(sanitized, candidate => Taken(parent, candidate, ignoring));
            return new NameCheck(false, check.Reason, suggestion);
        }

        if (!Taken(parent, name, ignoring))
        {
            return NameCheck.Ok;
        }

        return new NameCheck(
            false,
            "Another entry in this folder already uses that name.",
            EntryNames.MakeUnique(name, candidate => Taken(parent, candidate, ignoring)));
    }

    /// <summary>True when a sibling other than <paramref name="ignoring"/> already uses the name.</summary>
    /// <param name="parent">Folder to test.</param>
    /// <param name="name">Candidate name.</param>
    /// <param name="ignoring">Entry to ignore.</param>
    public static bool Taken(TreeNode parent, string name, uint? ignoring)
    {
        foreach (TreeNode child in parent.Children)
        {
            if (child.Id != ignoring && EntryNames.Equals(child.Name, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recursive plaintext size of a folder, computed once and cached until the subtree changes.</summary>
    /// <param name="folder">Folder to measure.</param>
    public long Rollup(TreeNode folder)
    {
        if (folder.RollupValid)
        {
            return folder.RollupBytes;
        }

        long total = 0;
        foreach (TreeNode child in folder.Children)
        {
            total += child.Kind == EntryKind.File ? child.Content?.Length ?? 0 : Rollup(child);
        }

        folder.RollupBytes = total;
        folder.RollupValid = true;
        return total;
    }

    /// <summary>Marks a node and every folder above it as needing a fresh rollup.</summary>
    /// <param name="node">Node whose size or membership changed.</param>
    public static void InvalidateRollups(TreeNode? node)
    {
        for (TreeNode? current = node; current is not null; current = current.Parent)
        {
            current.RollupValid = false;
        }
    }

    /// <summary>Moves a stored entry into the <see cref="EntryState.Changed"/> state.</summary>
    /// <param name="node">Node that was renamed, moved or annotated.</param>
    public static void MarkChanged(TreeNode node)
    {
        if (node.State == EntryState.Stored)
        {
            node.State = EntryState.Changed;
        }
    }

    /// <summary>Builds the immutable snapshot the public API hands out.</summary>
    /// <param name="node">Node to describe.</param>
    public EntryInfo Snapshot(TreeNode node) => new(
        new EntryId(node.Id),
        new EntryId(node.ParentId),
        node.Kind,
        node.Name,
        node.Kind == EntryKind.File ? node.Content?.Length ?? 0 : Rollup(node),
        node.Kind == EntryKind.Folder ? node.Children.Count : 0,
        ToUtc(node.CreatedUtcTicks),
        ToUtc(node.ModifiedUtcTicks),
        node.Comment,
        node.State);

    /// <summary>Case-insensitive substring search over entry names.</summary>
    /// <param name="nameSubstring">Text to look for.</param>
    /// <param name="scope">Subtree to search, or <see langword="null"/> for the whole vault.</param>
    /// <param name="maxResults">Maximum number of hits.</param>
    /// <param name="ct">Cancellation token.</param>
    public List<EntryInfo> Search(string nameSubstring, TreeNode? scope, int maxResults, CancellationToken ct)
    {
        var hits = new List<EntryInfo>();
        if (maxResults <= 0)
        {
            return hits;
        }

        int examined = 0;
        foreach (TreeNode node in Subtree(scope ?? Root))
        {
            if ((++examined & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (node.Id == 0)
            {
                continue;
            }

            if (nameSubstring.Length == 0 ||
                node.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(Snapshot(node));
                if (hits.Count >= maxResults)
                {
                    break;
                }
            }
        }

        return hits;
    }

    /// <summary>Largest tick count a <see cref="DateTime"/> can represent.</summary>
    public const long MaxTicks = 3155378975999999999L;

    /// <summary>Converts stored ticks to a UTC timestamp, clamping anything out of range to zero.</summary>
    /// <param name="ticks">Tick count from the index.</param>
    public static DateTimeOffset ToUtc(long ticks)
    {
        if (ticks is < 0 or > MaxTicks)
        {
            ticks = 0;
        }

        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    /// <summary>Converts a timestamp to the tick count stored in the index.</summary>
    /// <param name="value">Timestamp to convert.</param>
    public static long ToTicks(DateTimeOffset value)
    {
        long ticks = value.UtcDateTime.Ticks;
        return ticks is < 0 or > MaxTicks ? 0 : ticks;
    }

    /// <summary>Adds or removes one node from the aggregate counters.</summary>
    /// <param name="node">The node.</param>
    /// <param name="sign">+1 when attaching, -1 when detaching.</param>
    private void Count(TreeNode node, int sign)
    {
        if (node.Id == 0)
        {
            return;
        }

        if (node.Kind == EntryKind.File)
        {
            FileCount += sign;
            TotalPlaintextBytes += sign * (node.Content?.Length ?? 0);
        }
        else
        {
            FolderCount += sign;
        }
    }

    /// <summary>Pushes the children of a folder so a stack pops them by ascending id.</summary>
    /// <param name="stack">Depth-first stack.</param>
    /// <param name="folder">Folder whose children are pushed.</param>
    private static void PushByDescendingId(Stack<TreeNode> stack, TreeNode folder)
    {
        if (folder.Children.Count == 0)
        {
            return;
        }

        var ordered = new List<TreeNode>(folder.Children);
        ordered.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            stack.Push(ordered[i]);
        }
    }
}

/// <summary>
/// Explorer-like ordering: case-insensitive, but runs of digits compare as numbers so
/// <c>file2</c> sorts before <c>file10</c>. Ties fall back to an ordinal comparison so the order is total.
/// </summary>
internal sealed class NaturalNameComparer : IComparer<string>
{
    /// <summary>The shared instance; the comparer is stateless.</summary>
    public static readonly NaturalNameComparer Instance = new();

    /// <inheritdoc />
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

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                {
                    i++;
                }

                while (j < y.Length && char.IsAsciiDigit(y[j]))
                {
                    j++;
                }

                int numbers = CompareNumbers(x.AsSpan(startX, i - startX), y.AsSpan(startY, j - startY));
                if (numbers != 0)
                {
                    return numbers;
                }

                continue;
            }

            int letters = CompareChars(x[i], y[j]);
            if (letters != 0)
            {
                return letters;
            }

            i++;
            j++;
        }

        int rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }

    /// <summary>Compares two digit runs numerically, ignoring leading zeros.</summary>
    /// <param name="a">First run.</param>
    /// <param name="b">Second run.</param>
    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        ReadOnlySpan<char> trimmedA = a.TrimStart('0');
        ReadOnlySpan<char> trimmedB = b.TrimStart('0');
        if (trimmedA.Length != trimmedB.Length)
        {
            return trimmedA.Length < trimmedB.Length ? -1 : 1;
        }

        int compared = trimmedA.SequenceCompareTo(trimmedB);
        return compared != 0 ? compared : a.Length.CompareTo(b.Length);
    }

    /// <summary>Compares two characters case-insensitively.</summary>
    /// <param name="a">First character.</param>
    /// <param name="b">Second character.</param>
    private static int CompareChars(char a, char b) => char.ToUpperInvariant(a).CompareTo(char.ToUpperInvariant(b));
}
