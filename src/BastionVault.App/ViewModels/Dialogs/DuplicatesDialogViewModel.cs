using System.Globalization;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels.Dialogs;

/// <summary>One file in a group of identical files.</summary>
/// <param name="Id">The entry.</param>
/// <param name="ParentId">Its folder, so the explorer can show it.</param>
/// <param name="Path">Its in-vault path.</param>
public sealed record DuplicateFile(EntryId Id, EntryId ParentId, string Path);

/// <summary>Files whose contents are byte-for-byte the same.</summary>
/// <param name="Length">Plaintext length of each file.</param>
/// <param name="Files">The files, in the order the walk found them; at least two.</param>
public sealed record DuplicateGroup(long Length, IReadOnlyList<DuplicateFile> Files);

/// <summary>What the user decided in the duplicates dialog.</summary>
public abstract record DuplicatesDecision;

/// <summary>Delete these copies.</summary>
/// <param name="Ids">The entries to delete.</param>
public sealed record DeleteDuplicates(IReadOnlyList<EntryId> Ids) : DuplicatesDecision;

/// <summary>Show this file in the explorer.</summary>
/// <param name="File">The file to reveal.</param>
public sealed record RevealDuplicate(DuplicateFile File) : DuplicatesDecision;

/// <summary>One line of the dialog: a file with a tick box.</summary>
public sealed partial class DuplicateRow : ObservableObject
{
    private readonly Action<DuplicateRow> _changed;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Creates the row.</summary>
    /// <param name="file">The file.</param>
    /// <param name="changed">Called when the tick box changes.</param>
    public DuplicateRow(DuplicateFile file, Action<DuplicateRow> changed)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(changed);
        File = file;
        _changed = changed;
    }

    /// <summary>The file.</summary>
    public DuplicateFile File { get; }

    /// <summary>Its in-vault path.</summary>
    public string Path => File.Path;

    partial void OnIsSelectedChanged(bool value) => _changed(this);
}

/// <summary>One group in the dialog.</summary>
/// <param name="Length">Plaintext length of each file.</param>
/// <param name="Rows">The files.</param>
public sealed record DuplicateGroupRows(long Length, IReadOnlyList<DuplicateRow> Rows)
{
    /// <summary>"3 files · 4.2 MB each".</summary>
    public string Heading => string.Create(
        CultureInfo.CurrentCulture,
        $"{Rows.Count:N0} files · {OperationViewModel.FormatBytes(Length)} each");
}

/// <summary>
/// The result of a duplicate scan (UI-CONTRACT.md section 7): groups of identical files, each with
/// a tick box. "Keep the first of each" ticks every copy but the first; "Delete ticked" deletes
/// them in one undoable step; a row's "Show" reveals the file in the explorer. Nothing is deleted
/// until a save, like every other change.
/// </summary>
public sealed partial class DuplicatesDialogViewModel : DialogViewModelBase<DuplicatesDecision>
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionLine))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionLine))]
    private long _selectedBytes;

    /// <summary>Creates the dialog over a scan result.</summary>
    /// <param name="groups">The groups the scan found; empty when the vault has no duplicates.</param>
    /// <param name="filesScanned">How many files the scan looked at.</param>
    public DuplicatesDialogViewModel(IReadOnlyList<DuplicateGroup> groups, int filesScanned)
    {
        ArgumentNullException.ThrowIfNull(groups);

        Groups = [.. groups.Select(g => new DuplicateGroupRows(g.Length, [.. g.Files.Select(f => new DuplicateRow(f, OnRowChanged))]))];
        FilesScanned = filesScanned;

        int copies = groups.Sum(g => g.Files.Count - 1);
        long reclaimable = groups.Sum(g => g.Length * (g.Files.Count - 1));
        Title = groups.Count == 0
            ? "No duplicates"
            : $"{groups.Count:N0} group{(groups.Count == 1 ? string.Empty : "s")} of identical files";
        Summary = groups.Count == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{filesScanned:N0} files compared · every one is different")
            : string.Create(CultureInfo.CurrentCulture, $"{filesScanned:N0} files compared · {copies:N0} extra cop{(copies == 1 ? "y" : "ies")} · {OperationViewModel.FormatBytes(reclaimable)} reclaimable");
    }

    /// <summary>The groups, largest files first.</summary>
    public IReadOnlyList<DuplicateGroupRows> Groups { get; }

    /// <summary>How many files the scan looked at.</summary>
    public int FilesScanned { get; }

    /// <summary>"1,204 files compared · 7 extra copies · 31 MB reclaimable".</summary>
    public string Summary { get; }

    /// <summary>True when nothing is identical to anything else.</summary>
    public bool IsClean => Groups.Count == 0;

    /// <summary>"3 ticked · 12 MB".</summary>
    public string SelectionLine => SelectedCount == 0
        ? "Nothing ticked."
        : string.Create(CultureInfo.CurrentCulture, $"{SelectedCount:N0} ticked · {OperationViewModel.FormatBytes(SelectedBytes)}");

    /// <summary>Ticks every copy except the first one in each group.</summary>
    [RelayCommand]
    public void KeepFirst()
    {
        foreach (DuplicateGroupRows group in Groups)
        {
            for (int i = 0; i < group.Rows.Count; i++)
            {
                group.Rows[i].IsSelected = i > 0;
            }
        }
    }

    /// <summary>Clears every tick.</summary>
    [RelayCommand]
    public void KeepAll()
    {
        foreach (DuplicateGroupRows group in Groups)
        {
            foreach (DuplicateRow row in group.Rows)
            {
                row.IsSelected = false;
            }
        }
    }

    /// <summary>Closes with the ticked files to delete.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void DeleteSelected()
    {
        List<EntryId> ids = [.. Groups.SelectMany(g => g.Rows).Where(r => r.IsSelected).Select(r => r.File.Id)];
        if (ids.Count > 0)
        {
            Close(new DeleteDuplicates(ids));
        }
    }

    /// <summary>Closes and asks the explorer to show a file.</summary>
    /// <param name="row">The row whose file to show.</param>
    [RelayCommand]
    public void Reveal(DuplicateRow? row)
    {
        if (row is not null)
        {
            Close(new RevealDuplicate(row.File));
        }
    }

    private bool HasSelection() => SelectedCount > 0;

    private void OnRowChanged(DuplicateRow row)
    {
        int count = 0;
        long bytes = 0;
        foreach (DuplicateGroupRows group in Groups)
        {
            foreach (DuplicateRow r in group.Rows)
            {
                if (r.IsSelected)
                {
                    count++;
                    bytes += group.Length;
                }
            }
        }

        SelectedCount = count;
        SelectedBytes = bytes;
    }
}
