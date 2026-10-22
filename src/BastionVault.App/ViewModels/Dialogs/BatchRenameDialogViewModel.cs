using BastionVault.App.Services;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels.Dialogs;

/// <summary>An entry the batch rename may rename.</summary>
/// <param name="Id">The entry.</param>
/// <param name="ParentId">Its folder, for collision checks among the new names.</param>
/// <param name="Name">Its current name.</param>
/// <param name="IsFolder">True for a folder, whose whole name is the stem.</param>
public sealed record RenameCandidate(EntryId Id, EntryId ParentId, string Name, bool IsFolder);

/// <summary>One rename the dialog decided on.</summary>
/// <param name="Id">The entry.</param>
/// <param name="NewName">The name to give it.</param>
public sealed record RenameInstruction(EntryId Id, string NewName);

/// <summary>One line of the preview: what the entry is called now and what it would be called.</summary>
public sealed partial class BatchRenameRow : ObservableObject
{
    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string? _reason;

    [ObservableProperty]
    private bool _isChanged;

    /// <summary>Creates the row for a candidate.</summary>
    /// <param name="candidate">The entry.</param>
    public BatchRenameRow(RenameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        NewName = candidate.Name;
    }

    /// <summary>The entry.</summary>
    public RenameCandidate Candidate { get; }

    /// <summary>The current name.</summary>
    public string OldName => Candidate.Name;

    /// <summary>True when the new name was refused.</summary>
    public bool IsInvalid => Reason is not null;

    partial void OnReasonChanged(string? value) => OnPropertyChanged(nameof(IsInvalid));
}

/// <summary>
/// Renames several entries at once (UI-CONTRACT.md section 7). The user types a find-and-replace,
/// a prefix and a suffix; the preview shows every resulting name and asks Core's name check about
/// each one, so nothing invalid or colliding can be applied. The result is a list of individual
/// renames, each of them undoable on its own.
/// </summary>
public sealed partial class BatchRenameDialogViewModel : DialogViewModelBase<IReadOnlyList<RenameInstruction>>
{
    private readonly Func<RenameCandidate, string, NameCheck> _validate;

    [ObservableProperty]
    private string _find = string.Empty;

    [ObservableProperty]
    private string _replace = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private string _prefix = string.Empty;

    [ObservableProperty]
    private string _suffix = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApply;

    /// <summary>Creates the dialog over a selection.</summary>
    /// <param name="candidates">The entries to rename; at least one.</param>
    /// <param name="validate">
    /// Core's name check for a candidate and a proposed name, with the candidate itself excluded
    /// from the "already used" test.
    /// </param>
    public BatchRenameDialogViewModel(IReadOnlyList<RenameCandidate> candidates, Func<RenameCandidate, string, NameCheck> validate)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Count);

        _validate = validate;
        Rows = [.. candidates.Select(c => new BatchRenameRow(c))];
        Title = $"Rename {candidates.Count:N0} item{(candidates.Count == 1 ? string.Empty : "s")}";
        Recompute();
    }

    /// <summary>The preview, one row per entry, in selection order.</summary>
    public IReadOnlyList<BatchRenameRow> Rows { get; }

    /// <summary>Applies the renames that changed something.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    public void Apply()
    {
        if (!CanApply)
        {
            return;
        }

        List<RenameInstruction> instructions =
        [
            .. Rows.Where(r => r.IsChanged && !r.IsInvalid).Select(r => new RenameInstruction(r.Candidate.Id, r.NewName)),
        ];
        Close(instructions);
    }

    /// <inheritdoc />
    public override bool Accept()
    {
        if (!CanApply)
        {
            return false;
        }

        Apply();
        return true;
    }

    /// <summary>
    /// The new name for one entry: find-and-replace over the whole name, then the prefix in front
    /// and the suffix before the extension (a folder has no extension; its suffix goes at the end).
    /// </summary>
    /// <param name="name">The current name.</param>
    /// <param name="isFolder">True for a folder.</param>
    /// <param name="find">Text to replace; empty replaces nothing.</param>
    /// <param name="replace">What to put in its place.</param>
    /// <param name="matchCase">True to match <paramref name="find"/> exactly.</param>
    /// <param name="prefix">Put in front of the name.</param>
    /// <param name="suffix">Put before the extension.</param>
    public static string Transform(string name, bool isFolder, string find, string replace, bool matchCase, string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(name);

        string result = name;
        if (!string.IsNullOrEmpty(find))
        {
            result = result.Replace(find, replace ?? string.Empty, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(suffix))
        {
            int stem = isFolder ? result.Length : FileTypeCatalog.StemLength(result);
            result = result[..stem] + suffix + result[stem..];
        }

        if (!string.IsNullOrEmpty(prefix))
        {
            result = prefix + result;
        }

        return result;
    }

    partial void OnFindChanged(string value) => Recompute();

    partial void OnReplaceChanged(string value) => Recompute();

    partial void OnMatchCaseChanged(bool value) => Recompute();

    partial void OnPrefixChanged(string value) => Recompute();

    partial void OnSuffixChanged(string value) => Recompute();

    private void Recompute()
    {
        // New names are checked against each other as well as against the folder: two rows that
        // would end up with the same name in the same folder are both refused.
        var planned = new Dictionary<(EntryId Parent, string Name), int>();
        foreach (BatchRenameRow row in Rows)
        {
            string proposed = Transform(row.OldName, row.Candidate.IsFolder, Find, Replace, MatchCase, Prefix, Suffix).Trim();
            row.NewName = proposed;
            row.IsChanged = !string.Equals(proposed, row.OldName, StringComparison.Ordinal);

            (EntryId, string) key = (row.Candidate.ParentId, proposed.ToUpperInvariant());
            planned[key] = planned.GetValueOrDefault(key) + 1;
        }

        int changed = 0;
        int invalid = 0;
        foreach (BatchRenameRow row in Rows)
        {
            if (!row.IsChanged)
            {
                row.Reason = null;
                continue;
            }

            changed++;
            (EntryId, string) key = (row.Candidate.ParentId, row.NewName.ToUpperInvariant());
            if (planned[key] > 1)
            {
                row.Reason = "Two items would get this name.";
                invalid++;
                continue;
            }

            NameCheck check = row.NewName.Length == 0
                ? new NameCheck(false, "A name cannot be empty.", null)
                : _validate(row.Candidate, row.NewName);
            row.Reason = check.IsValid ? null : check.Reason ?? "That name cannot be used.";
            if (!check.IsValid)
            {
                invalid++;
            }
        }

        CanApply = changed > 0 && invalid == 0;
        Summary = changed == 0
            ? "Nothing changes yet."
            : invalid == 0
                ? $"{changed:N0} of {Rows.Count:N0} item{(Rows.Count == 1 ? string.Empty : "s")} will be renamed."
                : $"{invalid:N0} new name{(invalid == 1 ? string.Empty : "s")} cannot be used.";
    }
}
