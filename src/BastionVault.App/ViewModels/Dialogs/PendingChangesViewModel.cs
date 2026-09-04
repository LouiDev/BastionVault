using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels.Dialogs;

/// <summary>One pending entry as the popover lists it.</summary>
/// <param name="Path">In-vault path of the entry.</param>
/// <param name="State">Whether it was added or changed.</param>
/// <param name="Kind">Folder or file.</param>
/// <param name="Length">Plaintext bytes, for the right-hand column.</param>
public sealed record PendingItem(string Path, EntryState State, EntryKind Kind, long Length)
{
    /// <summary>Glyph resource key for the state pip.</summary>
    public string StateGlyph => State == EntryState.Added ? "Glyph.NewFolder" : "Glyph.Rename";

    /// <summary>The state as a word.</summary>
    public string StateText => State == EntryState.Added ? "added" : "changed";

    /// <summary>The size, formatted; folders show a dash.</summary>
    public string SizeText => Kind == EntryKind.Folder ? "-" : OperationViewModel.FormatBytes(Length);
}

/// <summary>
/// The pending-changes popover behind the title-bar dirty bullet: the counts a save would commit
/// and, under them, the entries themselves. Deletions have no entry left to list, so they are
/// reported as a count only.
/// </summary>
public sealed partial class PendingChangesViewModel : ObservableObject
{
    private readonly IVaultSession _session;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private PendingChanges _pending = new(0, 0, 0, 0, false, false);

    [ObservableProperty]
    private IReadOnlyList<PendingItem> _items = [];

    /// <summary>Creates the popover over a session.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="undo">Command that undoes the last change.</param>
    public PendingChangesViewModel(IVaultSession session, IAsyncRelayCommand undo)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        UndoCommand = undo;
        Refresh();
    }

    /// <summary>Undoes the last change; the same command the shell binds to Ctrl+Z.</summary>
    public IAsyncRelayCommand UndoCommand { get; }

    /// <summary>True when a save would commit something.</summary>
    public bool HasChanges => Pending.Any;

    /// <summary>"3 added · 1 changed · 2 deleted".</summary>
    public string CountsLine =>
        $"{Pending.Added} added · {Pending.Changed} changed · {Pending.Deleted} deleted";

    /// <summary>Bytes the next save has to write.</summary>
    public string BytesLine => Pending.BytesToWrite > 0
        ? $"{OperationViewModel.FormatBytes(Pending.BytesToWrite)} to write"
        : "nothing to write";

    /// <summary>True when a credential change is waiting for the next save.</summary>
    public bool HasCredentialChange => Pending.CredentialChangePending;

    /// <summary>Re-reads the session and rebuilds the list.</summary>
    public void Refresh()
    {
        Pending = _session.Pending;

        var items = new List<PendingItem>();
        Walk(EntryId.Root, "\\", items, 0);
        Items = items;

        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CountsLine));
        OnPropertyChanged(nameof(BytesLine));
        OnPropertyChanged(nameof(HasCredentialChange));
    }

    private void Walk(EntryId folder, string prefix, List<PendingItem> into, int depth)
    {
        // A vault can be deep; the popover is a summary, not a tree view.
        if (depth > 16 || into.Count >= 500)
        {
            return;
        }

        foreach (EntryInfo child in _session.GetChildren(folder))
        {
            string path = prefix.EndsWith('\\') ? prefix + child.Name : prefix + "\\" + child.Name;
            if (child.State != EntryState.Stored)
            {
                into.Add(new PendingItem(path, child.State, child.Kind, child.Length));
            }

            if (child.Kind == EntryKind.Folder)
            {
                Walk(child.Id, path, into, depth + 1);
            }
        }
    }
}
