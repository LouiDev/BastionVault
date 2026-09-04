using System.Text;
using BastionVault.Core.Format;

namespace BastionVault.Core.Session;

/// <summary>The in-memory tree mutations. Every one of them is applied under the session lock.</summary>
internal sealed partial class VaultSession
{
    /// <inheritdoc />
    public Task<EntryId> CreateFolderAsync(EntryId parent, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        TreeNode folder;
        TreeNode node;
        lock (_treeGate)
        {
            folder = RequireFolder(parent, nameof(parent));
            RequireValidName(folder, name, null);
            RequireDepth(TreeModel.DepthOf(folder) + 1);

            long now = TreeModel.ToTicks(Clock.UtcNow);
            node = new TreeNode
            {
                Id = Tree.AllocateId(),
                Kind = EntryKind.Folder,
                Name = name,
                CreatedUtcTicks = now,
                ModifiedUtcTicks = now,
                State = EntryState.Added,
            };

            Tree.Attach(node, folder);
            _undo.Push(new AddEntriesStep([(node, folder)], [], $"Create folder {name}"));
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, [new EntryId(node.Id)], parent);
        return Task.FromResult(new EntryId(node.Id));
    }

    /// <inheritdoc />
    public Task RenameAsync(EntryId entry, string newName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newName);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        EntryId parentId;
        lock (_treeGate)
        {
            TreeNode node = RequireNode(entry, nameof(entry));
            if (node.Id == 0)
            {
                throw new VaultOperationException(VaultErrorCode.InvalidMove, "The vault root cannot be renamed.");
            }

            if (string.Equals(node.Name, newName, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            TreeNode folder = node.Parent ?? Tree.Root;
            RequireValidName(folder, newName, node.Id);

            string oldName = node.Name;
            _undo.Push(new RenameStep(node, oldName, newName, node.State, node.ModifiedUtcTicks));
            node.Name = newName;
            TreeModel.MarkChanged(node);
            parentId = new EntryId(folder.Id);
        }

        MarkDirty();
        Raise(VaultChangeKind.EntryRenamed, [entry], parentId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetCommentAsync(EntryId entry, string comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comment);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        RequireValidComment(comment);

        EntryId parentId;
        lock (_treeGate)
        {
            TreeNode node = RequireNode(entry, nameof(entry));
            if (node.Id == 0)
            {
                throw new VaultOperationException(VaultErrorCode.InvalidMove, "The vault root carries no comment.");
            }

            if (string.Equals(node.Comment, comment, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            _undo.Push(new CommentStep(node, node.Comment, comment, node.State));
            node.Comment = comment;
            TreeModel.MarkChanged(node);
            parentId = new EntryId(node.ParentId);
        }

        MarkDirty();
        Raise(VaultChangeKind.EntryUpdated, [entry], parentId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        var moved = new List<EntryId>();
        lock (_treeGate)
        {
            TreeNode destination = RequireFolder(newParent, nameof(newParent));
            var plan = new List<(TreeNode Node, TreeNode OldParent, EntryState OldState)>();

            foreach (EntryId id in entries)
            {
                TreeNode node = RequireNode(id, nameof(entries));
                if (node.Id == 0)
                {
                    throw new VaultOperationException(VaultErrorCode.InvalidMove, "The vault root cannot be moved.");
                }

                if (TreeModel.IsSelfOrDescendantOf(destination, node))
                {
                    throw new VaultOperationException(
                        VaultErrorCode.InvalidMove,
                        $"{node.Name} cannot be moved into itself or into one of its own subfolders.");
                }

                if (ReferenceEquals(node.Parent, destination))
                {
                    continue;
                }

                if (TreeModel.Taken(destination, node.Name, null))
                {
                    throw new VaultOperationException(
                        VaultErrorCode.NameConflict,
                        $"The destination folder already contains an entry named {node.Name}.");
                }

                RequireDepth(TreeModel.DepthOf(destination) + 1 + TreeModel.HeightOf(node));
                plan.Add((node, node.Parent ?? Tree.Root, node.State));
            }

            if (plan.Count == 0)
            {
                return Task.CompletedTask;
            }

            foreach ((TreeNode node, TreeNode _, EntryState _) in plan)
            {
                Reparent(node, destination);
                TreeModel.MarkChanged(node);
                moved.Add(new EntryId(node.Id));
            }

            _undo.Push(new MoveStep(plan, destination));
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesMoved, moved, newParent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryId>> CopyAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        var copies = new List<EntryId>();
        lock (_treeGate)
        {
            TreeNode destination = RequireFolder(newParent, nameof(newParent));
            var added = new List<(TreeNode Node, TreeNode Parent)>();

            foreach (EntryId id in entries)
            {
                TreeNode node = RequireNode(id, nameof(entries));
                if (node.Id == 0)
                {
                    throw new VaultOperationException(VaultErrorCode.InvalidMove, "The vault root cannot be copied.");
                }

                if (TreeModel.IsSelfOrDescendantOf(destination, node))
                {
                    throw new VaultOperationException(
                        VaultErrorCode.InvalidMove,
                        $"{node.Name} cannot be copied into itself or into one of its own subfolders.");
                }

                RequireDepth(TreeModel.DepthOf(destination) + 1 + TreeModel.HeightOf(node));

                string name = UniqueSiblingName(node.Name, destination);
                TreeNode copy = CloneSubtree(node, name);
                AttachSubtree(copy, destination);
                added.Add((copy, destination));
                copies.Add(new EntryId(copy.Id));
            }

            if (added.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<EntryId>>(copies);
            }

            _undo.Push(new AddEntriesStep(
                added,
                [],
                added.Count == 1 ? $"Copy {added[0].Node.Name}" : $"Copy {added.Count} entries"));
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, copies, newParent);
        return Task.FromResult<IReadOnlyList<EntryId>>(copies);
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyList<EntryId> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        var removedIds = new List<EntryId>();
        EntryId parent = EntryId.Root;
        lock (_treeGate)
        {
            var removed = new List<(TreeNode Node, TreeNode Parent)>();
            var seen = new List<TreeNode>();

            foreach (EntryId id in entries)
            {
                TreeNode node = RequireNode(id, nameof(entries));
                if (node.Id == 0)
                {
                    throw new VaultOperationException(VaultErrorCode.InvalidMove, "The vault root cannot be deleted.");
                }

                // Deleting an ancestor already removes its descendants.
                bool covered = false;
                foreach (TreeNode other in seen)
                {
                    if (!ReferenceEquals(other, node) && TreeModel.IsSelfOrDescendantOf(node, other))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                {
                    continue;
                }

                seen.Add(node);
                removed.Add((node, node.Parent ?? Tree.Root));
            }

            if (removed.Count == 0)
            {
                return Task.CompletedTask;
            }

            parent = new EntryId(removed[0].Parent.Id);
            foreach ((TreeNode node, TreeNode _) in removed)
            {
                removedIds.Add(new EntryId(node.Id));
                DetachSubtree(node);
            }

            _undo.Push(new RemoveEntriesStep(
                removed,
                removed.Count == 1 ? $"Delete {removed[0].Node.Name}" : $"Delete {removed.Count} entries"));
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesRemoved, removedIds, parent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UndoAsync(CancellationToken ct)
    {
        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        lock (_treeGate)
        {
            if (_undo.Undo(this) is null)
            {
                return Task.CompletedTask;
            }
        }

        RefreshDirty();
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedoAsync(CancellationToken ct)
    {
        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        lock (_treeGate)
        {
            if (_undo.Redo(this) is null)
            {
                return Task.CompletedTask;
            }
        }

        RefreshDirty();
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <summary>Copies a subtree, giving every node a fresh id and content that a save must re-encrypt.</summary>
    /// <param name="source">Root of the subtree to copy.</param>
    /// <param name="name">Name of the copy.</param>
    private TreeNode CloneSubtree(TreeNode source, string name)
    {
        var copy = new TreeNode
        {
            Id = Tree.AllocateId(),
            Kind = source.Kind,
            Name = name,
            Comment = source.Comment,
            CreatedUtcTicks = source.CreatedUtcTicks,
            ModifiedUtcTicks = source.ModifiedUtcTicks,
            State = EntryState.Added,
            Content = source.Content?.AsCopy(),
        };

        foreach (TreeNode child in source.Children)
        {
            TreeNode childCopy = CloneSubtree(child, child.Name);
            childCopy.Parent = copy;
            copy.Children.Add(childCopy);
        }

        return copy;
    }

    /// <summary>Recomputes the dirty flag after an undo or redo.</summary>
    private void RefreshDirty()
    {
        bool dirty = _pendingCredentials is not null || _deletedStored.Count > 0;
        if (!dirty)
        {
            lock (_treeGate)
            {
                foreach (TreeNode node in Tree.CanonicalOrder())
                {
                    if (node.State != EntryState.Stored)
                    {
                        dirty = true;
                        break;
                    }
                }
            }
        }

        if (dirty)
        {
            MarkDirty();
        }
        else
        {
            ClearDirty();
        }
    }

    /// <summary>Rejects a name that is invalid or already taken.</summary>
    /// <param name="parent">Folder the name would live in.</param>
    /// <param name="name">Candidate name.</param>
    /// <param name="ignoring">Entry to ignore during the uniqueness check.</param>
    private static void RequireValidName(TreeNode parent, string name, uint? ignoring)
    {
        NameCheck check = EntryNames.Validate(name);
        if (!check.IsValid)
        {
            throw new VaultOperationException(VaultErrorCode.NameInvalid, check.Reason ?? "The name is not valid.");
        }

        if (TreeModel.Taken(parent, name, ignoring))
        {
            throw new VaultOperationException(
                VaultErrorCode.NameConflict,
                $"This folder already contains an entry named {name}.");
        }
    }

    /// <summary>Rejects a comment that the format cannot store.</summary>
    /// <param name="comment">Candidate comment.</param>
    private static void RequireValidComment(string comment)
    {
        foreach (char c in comment)
        {
            bool control = (c <= '\u001F' && c is not '\t' and not '\n' and not '\r') ||
                           c is >= '\u007F' and <= '\u009F';
            if (control)
            {
                throw new VaultOperationException(
                    VaultErrorCode.NameInvalid,
                    $"A comment must not contain the control character U+{(int)c:X4}.");
            }

            // A comment is shown verbatim next to the name, so it gets the name filter too: a bidi
            // override or a line separator can otherwise reverse or split the text a user reads.
            if (EntryNames.IsBidiOrBom(c))
            {
                throw new VaultOperationException(
                    VaultErrorCode.NameInvalid,
                    $"A comment must not contain invisible formatting characters (found U+{(int)c:X4}).");
            }
        }

        int bytes = Encoding.UTF8.GetByteCount(comment);
        if (bytes > VaultLimits.MaxCommentBytes)
        {
            throw new VaultOperationException(
                VaultErrorCode.NameInvalid,
                $"A comment may be at most {VaultLimits.MaxCommentBytes} bytes long (this one is {bytes}).");
        }
    }

    /// <summary>
    /// Picks a free sibling name, and proves that what the uniquifier produced is still a valid name.
    /// A name that fails section 6.1 would be accepted into the tree here and then refused by the index
    /// serializer, so every later save would fail until the entry was found and renamed.
    /// </summary>
    /// <param name="wanted">The name the entry would like to keep.</param>
    /// <param name="destination">Folder the entry is going into.</param>
    internal static string UniqueSiblingName(string wanted, TreeNode destination)
    {
        string unique = EntryNames.MakeUnique(wanted, candidate => TreeModel.Taken(destination, candidate, null));
        if (EntryNames.Validate(unique).IsValid)
        {
            return unique;
        }

        string sanitized = EntryNames.Sanitize(unique);
        string fallback = EntryNames.MakeUnique(sanitized, candidate => TreeModel.Taken(destination, candidate, null));
        return EntryNames.Validate(fallback).IsValid ? fallback : sanitized;
    }

    /// <summary>Rejects a tree that would become deeper than the format allows.</summary>
    /// <param name="depth">Depth the deepest affected entry would sit at.</param>
    private static void RequireDepth(int depth)
    {
        if (depth > VaultLimits.MaxDepth)
        {
            throw new VaultOperationException(
                VaultErrorCode.InvalidMove,
                $"The vault allows {VaultLimits.MaxDepth} levels of folders; this would create level {depth}.");
        }
    }
}
