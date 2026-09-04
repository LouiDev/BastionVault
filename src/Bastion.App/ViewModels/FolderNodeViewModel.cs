using System.Collections.ObjectModel;
using Bastion.App.Controls;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bastion.App.ViewModels;

/// <summary>
/// One folder in the tree. Children load on first expand from
/// <see cref="IVaultSession.GetChildren"/>, and <see cref="IsExpanded"/> and
/// <see cref="IsSelected"/> are the view model's own state so nothing ever reaches for a
/// <c>TreeViewItem</c> (UI-CONTRACT.md section 1.6).
/// </summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    /// <summary>
    /// How many entries the pending-descendant probe looks at before giving up. A vault can hold
    /// a million files; a 4 px dot is not worth walking all of them on every change.
    /// </summary>
    private const int PendingProbeBudget = 4096;

    private readonly IVaultSession _session;
    private readonly Action<FolderNodeViewModel>? _selected;

    private bool _childrenLoaded;
    private bool _hasProbedSubfolders;
    private bool _hasSubfolders;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private PipState _pip;

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isMasked;

    /// <summary>Creates a node.</summary>
    /// <param name="session">The open session the node reads from.</param>
    /// <param name="id">Identifier of the folder; <see cref="EntryId.Root"/> for the vault root.</param>
    /// <param name="name">Display name of the folder.</param>
    /// <param name="parent">Parent node, or <see langword="null"/> for the root.</param>
    /// <param name="selected">Called when the node becomes the selected one.</param>
    public FolderNodeViewModel(
        IVaultSession session,
        EntryId id,
        string name,
        FolderNodeViewModel? parent,
        Action<FolderNodeViewModel>? selected)
        : this(session, id, name, parent, selected, isPlaceholder: false)
    {
    }

    private FolderNodeViewModel(
        IVaultSession session,
        EntryId id,
        string name,
        FolderNodeViewModel? parent,
        Action<FolderNodeViewModel>? selected,
        bool isPlaceholder)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _selected = selected;
        _name = name;
        Id = id;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        IsPlaceholder = isPlaceholder;

        if (!isPlaceholder && HasSubfolders)
        {
            // A placeholder gives the row its chevron before anything is loaded. Expanding is
            // synchronous, so it is replaced before it can ever be painted.
            Children.Add(CreatePlaceholder());
        }
    }

    /// <summary>Identifier of the folder.</summary>
    public EntryId Id { get; }

    /// <summary>Parent node, or <see langword="null"/> for the root.</summary>
    public FolderNodeViewModel? Parent { get; }

    /// <summary>Distance from the root, used by the drop behaviour for indentation maths.</summary>
    public int Depth { get; }

    /// <summary>Child folders; a single placeholder until the node is first expanded.</summary>
    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    /// <summary>True for the placeholder row that gives an unexpanded node its chevron.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>True for the vault root, which is drawn with a different glyph.</summary>
    public bool IsRoot => Id.IsRoot;

    /// <summary>
    /// What the tree row shows: the folder name, or a mask while panic mode is on. The root is
    /// masked with everything else, so panic mode reads as uniform rather than leaving one
    /// legible label at the top of the tree.
    /// </summary>
    public string DisplayName => IsMasked ? EntryItemViewModel.MaskName(Name) : Name;

    /// <summary>True when this folder has at least one subfolder.</summary>
    public bool HasSubfolders
    {
        get
        {
            if (!_hasProbedSubfolders)
            {
                _hasSubfolders = _session.GetChildren(Id).Any(c => c.Kind == EntryKind.Folder);
                _hasProbedSubfolders = true;
            }

            return _hasSubfolders;
        }
    }

    /// <summary>Expands every ancestor so this node is visible in the tree.</summary>
    public void ExpandAncestors()
    {
        for (FolderNodeViewModel? node = Parent; node is not null; node = node.Parent)
        {
            node.IsExpanded = true;
        }
    }

    /// <summary>
    /// Re-reads this node from the session: name, pending pip and, when the children are already
    /// loaded, the child list - reconciled by id so expansion and selection survive.
    /// </summary>
    public void Refresh()
    {
        if (IsPlaceholder)
        {
            return;
        }

        EntryInfo? info = IsRoot ? null : _session.Find(Id);
        if (!IsRoot && info is null)
        {
            // The folder is gone; the parent's reconcile will drop this node in a moment.
            return;
        }

        if (info is not null)
        {
            Name = info.Name;
        }

        _hasProbedSubfolders = false;

        // Children first, then this node's pip: a loaded child has just worked out its own
        // answer, so the parent can read it instead of walking the same subtree again.
        if (_childrenLoaded)
        {
            Reconcile();
        }
        else if (Children.Count == 0 && HasSubfolders)
        {
            Children.Add(CreatePlaceholder());
        }
        else if (Children.Count > 0 && Children[0].IsPlaceholder && !HasSubfolders)
        {
            Children.Clear();
        }

        Pip = ComputePip(info);
    }

    /// <summary>Finds a descendant by id among the nodes that are already materialised.</summary>
    /// <param name="id">Folder to look for.</param>
    /// <returns>The node, or <see langword="null"/> when it is not loaded.</returns>
    public FolderNodeViewModel? FindLoaded(EntryId id)
    {
        if (Id == id)
        {
            return this;
        }

        foreach (FolderNodeViewModel child in Children)
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            FolderNodeViewModel? hit = child.FindLoaded(id);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the child folders if they are not loaded yet. Expanding calls this; so does the
    /// explorer when it needs to reveal a folder the user navigated to from the list.
    /// </summary>
    public void EnsureChildren()
    {
        if (_childrenLoaded || IsPlaceholder)
        {
            return;
        }

        _childrenLoaded = true;
        Reconcile();
    }

    /// <summary>True when <paramref name="candidate"/> is this node or one of its descendants.</summary>
    /// <param name="candidate">Folder to test.</param>
    public bool IsSelfOrDescendantOf(EntryId candidate)
    {
        for (FolderNodeViewModel? node = this; node is not null; node = node.Parent)
        {
            if (node.Id == candidate)
            {
                return true;
            }
        }

        return false;
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnIsMaskedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayName));

        foreach (FolderNodeViewModel child in Children)
        {
            child.IsMasked = value;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureChildren();
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsPlaceholder)
        {
            _selected?.Invoke(this);
        }
    }

    private FolderNodeViewModel CreatePlaceholder() =>
        new(_session, Id, string.Empty, this, null, isPlaceholder: true);

    private void Reconcile()
    {
        List<EntryInfo> folders = [.. _session.GetChildren(Id).Where(c => c.Kind == EntryKind.Folder)];

        // Drop the placeholder and anything that no longer exists.
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            FolderNodeViewModel existing = Children[i];
            if (existing.IsPlaceholder || folders.All(f => f.Id != existing.Id))
            {
                Children.RemoveAt(i);
            }
        }

        for (int i = 0; i < folders.Count; i++)
        {
            EntryInfo info = folders[i];
            int at = IndexOf(info.Id);

            if (at < 0)
            {
                var created = new FolderNodeViewModel(_session, info.Id, info.Name, this, _selected)
                {
                    IsMasked = IsMasked,
                };
                created.Refresh();
                Children.Insert(Math.Min(i, Children.Count), created);
                continue;
            }

            if (at != i)
            {
                Children.Move(at, i);
            }

            Children[i].Refresh();
        }
    }

    private int IndexOf(EntryId id)
    {
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Works out whether this node shows a pending pip. Run bottom-up: <see cref="Refresh"/>
    /// reconciles the children first, so a node whose children are loaded answers from their
    /// pips and one pass over its own direct children, and only a collapsed node pays for the
    /// bounded subtree probe. Computing it top-down instead cost (loaded nodes) x (subtree size)
    /// per refresh, on the UI thread, on every coalesced change - which is a stutter in exactly
    /// the operation that raises the most change events.
    /// </summary>
    /// <param name="info">This folder's snapshot, or null for the root.</param>
    private PipState ComputePip(EntryInfo? info)
    {
        if (info is { State: EntryState.Added })
        {
            return PipState.Added;
        }

        if (info is { State: EntryState.Changed })
        {
            return PipState.Changed;
        }

        foreach (EntryInfo child in _session.GetChildren(Id))
        {
            if (child.State != EntryState.Stored)
            {
                return PipState.Changed;
            }
        }

        if (_childrenLoaded)
        {
            // Every subfolder is represented by a loaded child that has already answered for its
            // own subtree, and the direct files were just checked above.
            foreach (FolderNodeViewModel child in Children)
            {
                if (!child.IsPlaceholder && child.Pip != PipState.None)
                {
                    return PipState.Changed;
                }
            }

            return PipState.None;
        }

        int budget = PendingProbeBudget;
        return HasPendingDescendant(Id, ref budget) ? PipState.Changed : PipState.None;
    }

    private bool HasPendingDescendant(EntryId folder, ref int budget)
    {
        foreach (EntryInfo child in _session.GetChildren(folder))
        {
            if (--budget < 0)
            {
                return false;
            }

            if (child.State != EntryState.Stored)
            {
                return true;
            }

            if (child.Kind == EntryKind.Folder && HasPendingDescendant(child.Id, ref budget))
            {
                return true;
            }
        }

        return false;
    }
}
