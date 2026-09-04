namespace BastionVault.Core.Session;

/// <summary>
/// One reversible tree edit. A step keeps the detached nodes themselves, so undo and redo restore the
/// original <see cref="EntryId"/>s and the original content references (API.md rule 7).
/// </summary>
internal abstract class UndoStep
{
    /// <summary>Human-readable description shown by the UI.</summary>
    public abstract string Description { get; }

    /// <summary>True when the step carries a pending credential change and must be dropped on lock.</summary>
    public virtual bool IsCredentialChange => false;

    /// <summary>Reverts the step.</summary>
    /// <param name="session">Session the step belongs to.</param>
    public abstract void Undo(VaultSession session);

    /// <summary>Applies the step again.</summary>
    /// <param name="session">Session the step belongs to.</param>
    public abstract void Redo(VaultSession session);
}

/// <summary>
/// Entries were added (new folder, in-vault copy, import). The step may also carry entries that the
/// operation replaced, so undo puts them back.
/// </summary>
internal sealed class AddEntriesStep : UndoStep
{
    private readonly List<(TreeNode Node, TreeNode Parent)> _added;
    private readonly List<(TreeNode Node, TreeNode Parent)> _replaced;
    private readonly string _description;

    /// <summary>Records an addition.</summary>
    /// <param name="added">Nodes that were attached, with the folder they went into.</param>
    /// <param name="replaced">Nodes that were removed to make room, with the folder they came from.</param>
    /// <param name="description">Description shown by the UI.</param>
    public AddEntriesStep(
        IEnumerable<(TreeNode Node, TreeNode Parent)> added,
        IEnumerable<(TreeNode Node, TreeNode Parent)> replaced,
        string description)
    {
        _added = [.. added];
        _replaced = [.. replaced];
        _description = description;
    }

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override void Undo(VaultSession session)
    {
        for (int i = _added.Count - 1; i >= 0; i--)
        {
            session.DetachSubtree(_added[i].Node);
        }

        foreach ((TreeNode node, TreeNode parent) in _replaced)
        {
            session.AttachSubtree(node, parent);
        }
    }

    /// <inheritdoc />
    public override void Redo(VaultSession session)
    {
        for (int i = _replaced.Count - 1; i >= 0; i--)
        {
            session.DetachSubtree(_replaced[i].Node);
        }

        foreach ((TreeNode node, TreeNode parent) in _added)
        {
            session.AttachSubtree(node, parent);
        }
    }
}

/// <summary>Entries were deleted.</summary>
internal sealed class RemoveEntriesStep : UndoStep
{
    private readonly List<(TreeNode Node, TreeNode Parent)> _removed;
    private readonly string _description;

    /// <summary>Records a deletion.</summary>
    /// <param name="removed">Nodes that were detached, with the folder they came from.</param>
    /// <param name="description">Description shown by the UI.</param>
    public RemoveEntriesStep(IEnumerable<(TreeNode Node, TreeNode Parent)> removed, string description)
    {
        _removed = [.. removed];
        _description = description;
    }

    /// <inheritdoc />
    public override string Description => _description;

    /// <inheritdoc />
    public override void Undo(VaultSession session)
    {
        foreach ((TreeNode node, TreeNode parent) in _removed)
        {
            session.AttachSubtree(node, parent);
        }
    }

    /// <inheritdoc />
    public override void Redo(VaultSession session)
    {
        for (int i = _removed.Count - 1; i >= 0; i--)
        {
            session.DetachSubtree(_removed[i].Node);
        }
    }
}

/// <summary>An entry was renamed.</summary>
internal sealed class RenameStep : UndoStep
{
    private readonly TreeNode _node;
    private readonly string _oldName;
    private readonly string _newName;
    private readonly EntryState _oldState;
    private readonly long _oldModifiedTicks;

    /// <summary>Records a rename.</summary>
    /// <param name="node">The renamed node.</param>
    /// <param name="oldName">Name before the rename.</param>
    /// <param name="newName">Name after the rename.</param>
    /// <param name="oldState">State before the rename.</param>
    /// <param name="oldModifiedTicks">Modification time before the rename.</param>
    public RenameStep(TreeNode node, string oldName, string newName, EntryState oldState, long oldModifiedTicks)
    {
        _node = node;
        _oldName = oldName;
        _newName = newName;
        _oldState = oldState;
        _oldModifiedTicks = oldModifiedTicks;
    }

    /// <inheritdoc />
    public override string Description => $"Rename {_oldName} to {_newName}";

    /// <inheritdoc />
    public override void Undo(VaultSession session)
    {
        _node.Name = _oldName;
        _node.State = _oldState;
        _node.ModifiedUtcTicks = _oldModifiedTicks;
    }

    /// <inheritdoc />
    public override void Redo(VaultSession session)
    {
        _node.Name = _newName;
        TreeModel.MarkChanged(_node);
    }
}

/// <summary>An entry comment was replaced.</summary>
internal sealed class CommentStep : UndoStep
{
    private readonly TreeNode _node;
    private readonly string _oldComment;
    private readonly string _newComment;
    private readonly EntryState _oldState;

    /// <summary>Records a comment change.</summary>
    /// <param name="node">The annotated node.</param>
    /// <param name="oldComment">Comment before the change.</param>
    /// <param name="newComment">Comment after the change.</param>
    /// <param name="oldState">State before the change.</param>
    public CommentStep(TreeNode node, string oldComment, string newComment, EntryState oldState)
    {
        _node = node;
        _oldComment = oldComment;
        _newComment = newComment;
        _oldState = oldState;
    }

    /// <inheritdoc />
    public override string Description => $"Change the comment on {_node.Name}";

    /// <inheritdoc />
    public override void Undo(VaultSession session)
    {
        _node.Comment = _oldComment;
        _node.State = _oldState;
    }

    /// <inheritdoc />
    public override void Redo(VaultSession session)
    {
        _node.Comment = _newComment;
        TreeModel.MarkChanged(_node);
    }
}

/// <summary>Entries were moved to another folder.</summary>
internal sealed class MoveStep : UndoStep
{
    private readonly List<(TreeNode Node, TreeNode OldParent, EntryState OldState)> _moves;
    private readonly TreeNode _newParent;

    /// <summary>Records a move.</summary>
    /// <param name="moves">Moved nodes with the folder they came from and their previous state.</param>
    /// <param name="newParent">Destination folder.</param>
    public MoveStep(IEnumerable<(TreeNode Node, TreeNode OldParent, EntryState OldState)> moves, TreeNode newParent)
    {
        _moves = [.. moves];
        _newParent = newParent;
    }

    /// <inheritdoc />
    public override string Description =>
        _moves.Count == 1 ? $"Move {_moves[0].Node.Name}" : $"Move {_moves.Count} entries";

    /// <inheritdoc />
    public override void Undo(VaultSession session)
    {
        for (int i = _moves.Count - 1; i >= 0; i--)
        {
            (TreeNode node, TreeNode oldParent, EntryState oldState) = _moves[i];
            session.Reparent(node, oldParent);
            node.State = oldState;
        }
    }

    /// <inheritdoc />
    public override void Redo(VaultSession session)
    {
        foreach ((TreeNode node, TreeNode _, EntryState _) in _moves)
        {
            session.Reparent(node, _newParent);
            TreeModel.MarkChanged(node);
        }
    }
}

/// <summary>A password, keyfile or KDF change became pending.</summary>
internal sealed class CredentialStep : UndoStep
{
    private readonly PendingCredentials? _before;
    private readonly PendingCredentials _after;

    /// <summary>Records a credential change.</summary>
    /// <param name="before">The change that was pending before, if any.</param>
    /// <param name="after">The change that is pending now.</param>
    public CredentialStep(PendingCredentials? before, PendingCredentials after)
    {
        _before = before;
        _after = after;
    }

    /// <inheritdoc />
    public override string Description => "Change the vault credentials";

    /// <inheritdoc />
    public override bool IsCredentialChange => true;

    /// <inheritdoc />
    public override void Undo(VaultSession session) => session.SetPendingCredentials(_before);

    /// <inheritdoc />
    public override void Redo(VaultSession session) => session.SetPendingCredentials(_after);
}

/// <summary>The undo journal of a session. Cleared by a successful save and by discarding changes.</summary>
internal sealed class UndoStack
{
    private readonly List<UndoStep> _undo = [];
    private readonly List<UndoStep> _redo = [];

    /// <summary>True when <see cref="Undo"/> would do something.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when <see cref="Redo"/> would do something.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Description of the next undo step, or <see langword="null"/>.</summary>
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    /// <summary>Description of the next redo step, or <see langword="null"/>.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Records a step that has already been applied and drops the redo branch.</summary>
    /// <param name="step">The step.</param>
    public void Push(UndoStep step)
    {
        _undo.Add(step);
        _redo.Clear();
    }

    /// <summary>Reverts the newest step.</summary>
    /// <param name="session">Session the steps belong to.</param>
    /// <returns>The step that was reverted, or <see langword="null"/> when there was none.</returns>
    public UndoStep? Undo(VaultSession session)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        UndoStep step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        step.Undo(session);
        _redo.Add(step);
        return step;
    }

    /// <summary>Applies the newest undone step again.</summary>
    /// <param name="session">Session the steps belong to.</param>
    /// <returns>The step that was applied, or <see langword="null"/> when there was none.</returns>
    public UndoStep? Redo(VaultSession session)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        UndoStep step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        step.Redo(session);
        _undo.Add(step);
        return step;
    }

    /// <summary>Drops the whole journal.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Drops every step that carries pending credential material (used by <see cref="IVaultSession.Lock"/>).</summary>
    public void DropCredentialSteps()
    {
        _undo.RemoveAll(static step => step.IsCredentialChange);
        _redo.RemoveAll(static step => step.IsCredentialChange);
    }
}
