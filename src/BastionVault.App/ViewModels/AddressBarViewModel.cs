using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels;

/// <summary>
/// One segment of the breadcrumb. The chevron after a crumb opens a dropdown of the folders that
/// sit next to it, which is how a user hops sideways without going up first.
/// </summary>
public sealed partial class CrumbViewModel : ObservableObject
{
    private readonly IVaultSession _session;
    private readonly EntryId _parent;
    private readonly Action<EntryId> _navigate;

    [ObservableProperty]
    private bool _isDropDownOpen;

    [ObservableProperty]
    private IReadOnlyList<CrumbSibling> _siblings = [];

    /// <summary>Creates a crumb.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="id">Folder the crumb points at.</param>
    /// <param name="parent">Folder that contains it; the dropdown lists that folder's subfolders.</param>
    /// <param name="name">Text of the crumb.</param>
    /// <param name="isLast">True for the folder currently being shown.</param>
    /// <param name="navigate">Called with the folder to go to.</param>
    public CrumbViewModel(IVaultSession session, EntryId id, EntryId parent, string name, bool isLast, Action<EntryId> navigate)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(navigate);

        _session = session;
        _parent = parent;
        _navigate = navigate;
        Id = id;
        Name = name;
        IsLast = isLast;
    }

    /// <summary>Folder the crumb points at.</summary>
    public EntryId Id { get; }

    /// <summary>Text of the crumb.</summary>
    public string Name { get; }

    /// <summary>True for the folder currently being shown; it is drawn in primary text.</summary>
    public bool IsLast { get; }

    /// <summary>Goes to this crumb's folder.</summary>
    [RelayCommand]
    public void Navigate() => _navigate(Id);

    /// <summary>Fills <see cref="Siblings"/> and opens the dropdown.</summary>
    [RelayCommand]
    public void OpenDropDown()
    {
        Siblings =
        [
            .. _session.GetChildren(_parent)
                .Where(c => c.Kind == EntryKind.Folder)
                .Select(c => new CrumbSibling(c.Id, c.Name, c.Id == Id, GoTo)),
        ];

        IsDropDownOpen = Siblings.Count > 0;
    }

    private void GoTo(EntryId id)
    {
        IsDropDownOpen = false;
        _navigate(id);
    }
}

/// <summary>One row of a crumb's sibling dropdown.</summary>
public sealed partial class CrumbSibling
{
    private readonly Action<EntryId> _navigate;

    /// <summary>Creates a sibling row.</summary>
    /// <param name="id">Folder the row points at.</param>
    /// <param name="name">Folder name.</param>
    /// <param name="isCurrent">True when this is the crumb the dropdown hangs off.</param>
    /// <param name="navigate">Called with the folder to go to.</param>
    public CrumbSibling(EntryId id, string name, bool isCurrent, Action<EntryId> navigate)
    {
        _navigate = navigate;
        Id = id;
        Name = name;
        IsCurrent = isCurrent;
    }

    /// <summary>Folder the row points at.</summary>
    public EntryId Id { get; }

    /// <summary>Folder name.</summary>
    public string Name { get; }

    /// <summary>True when the row is the folder the dropdown hangs off.</summary>
    public bool IsCurrent { get; }

    /// <summary>Goes to this folder.</summary>
    [RelayCommand]
    public void Navigate() => _navigate(Id);
}

/// <summary>
/// The address bar. It has two faces: a row of crumbs with sibling dropdowns, and - on Ctrl+L,
/// Alt+D, F4 or a click on the empty part of the row - an editable path box with autocomplete over
/// the folders that actually exist. Escape reverts to the crumbs; Enter resolves the path through
/// <see cref="IVaultSession.TryResolvePath"/>, because the UI never assembles a path itself
/// (FORMAT.md section 6.3).
/// </summary>
public sealed partial class AddressBarViewModel : ObservableObject
{
    /// <summary>At most this many completions are offered; the list is a hint, not a directory.</summary>
    private const int MaxSuggestions = 12;

    private readonly IVaultSession _session;
    private readonly Action<EntryId> _navigate;

    private EntryId _folder = EntryId.Root;

    [ObservableProperty]
    private IReadOnlyList<CrumbViewModel> _crumbs = [];

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "\\";

    [ObservableProperty]
    private IReadOnlyList<string> _suggestions = [];

    [ObservableProperty]
    private bool _isSuggestionListOpen;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isMasked;

    /// <summary>Creates the address bar over a session.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="navigate">Called with the folder the user asked for.</param>
    public AddressBarViewModel(IVaultSession session, Action<EntryId> navigate)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(navigate);

        _session = session;
        _navigate = navigate;
        Refresh(EntryId.Root);
    }

    /// <summary>Raised when the view should put the caret in the path box.</summary>
    public event EventHandler? EditRequested;

    /// <summary>The path currently being shown, as Core formats it.</summary>
    public string Path { get; private set; } = "\\";

    /// <summary>Rebuilds the crumbs for a folder and leaves edit mode.</summary>
    /// <param name="folder">The folder now being shown.</param>
    public void Refresh(EntryId folder)
    {
        _folder = folder;
        Path = _session.FormatPath(folder);

        var crumbs = new List<CrumbViewModel>
        {
            new(_session, EntryId.Root, EntryId.Root, "Vault", folder.IsRoot, _navigate),
        };

        IReadOnlyList<EntryInfo> chain = folder.IsRoot ? [] : _session.GetAncestors(folder);
        for (int i = 0; i < chain.Count; i++)
        {
            crumbs.Add(new CrumbViewModel(
                _session,
                chain[i].Id,
                i == 0 ? EntryId.Root : chain[i - 1].Id,
                IsMasked ? EntryItemViewModel.MaskName(chain[i].Name) : chain[i].Name,
                i == chain.Count - 1,
                _navigate));
        }

        Crumbs = crumbs;

        if (!IsEditing)
        {
            EditText = Path;
        }

        HasError = false;
    }

    /// <summary>Switches to the editable path box, preselected, and asks the view for focus.</summary>
    [RelayCommand]
    public void BeginEdit()
    {
        EditText = Path;
        HasError = false;
        Suggestions = [];
        IsSuggestionListOpen = false;
        IsEditing = true;
        EditRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Leaves edit mode and puts the crumbs back, discarding whatever was typed.</summary>
    [RelayCommand]
    public void CancelEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;
        IsSuggestionListOpen = false;
        Suggestions = [];
        HasError = false;
        EditText = Path;
    }

    /// <summary>
    /// Resolves what was typed and navigates. An unresolvable path leaves the box open and marks
    /// it, rather than silently snapping back.
    /// </summary>
    /// <returns>True when the path resolved and the explorer was asked to navigate.</returns>
    public bool TryCommit()
    {
        string text = (EditText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            text = "\\";
        }

        if (!_session.TryResolvePath(text, out EntryId id) || _session.Find(id) is { Kind: EntryKind.File })
        {
            HasError = true;
            return false;
        }

        IsEditing = false;
        IsSuggestionListOpen = false;
        Suggestions = [];
        HasError = false;
        _navigate(id);
        return true;
    }

    /// <summary>Resolves what was typed and navigates; the command face of <see cref="TryCommit"/>.</summary>
    [RelayCommand]
    public void Commit() => TryCommit();

    /// <summary>Accepts a completion without leaving the box, so the user can keep typing deeper.</summary>
    /// <param name="suggestion">The completion the user picked.</param>
    [RelayCommand]
    public void ApplySuggestion(string? suggestion)
    {
        if (string.IsNullOrEmpty(suggestion))
        {
            return;
        }

        EditText = suggestion;
        IsSuggestionListOpen = false;
    }

    partial void OnEditTextChanged(string value)
    {
        if (!IsEditing)
        {
            return;
        }

        HasError = false;
        Suggestions = Complete(value);
        IsSuggestionListOpen = Suggestions.Count > 0;
    }

    /// <summary>
    /// Completions for a partially typed path: the folders under the last complete segment whose
    /// names start with what has been typed after it.
    /// </summary>
    /// <param name="text">What is in the box.</param>
    internal IReadOnlyList<string> Complete(string? text)
    {
        string typed = text ?? string.Empty;
        if (typed.Length == 0)
        {
            typed = "\\";
        }

        int lastSeparator = typed.LastIndexOf('\\');
        string parentPath = lastSeparator <= 0 ? "\\" : typed[..lastSeparator];
        string prefix = lastSeparator < 0 ? typed : typed[(lastSeparator + 1)..];

        if (!_session.TryResolvePath(parentPath, out EntryId parent))
        {
            return [];
        }

        string basePath = _session.FormatPath(parent);
        string separator = basePath.EndsWith('\\') ? string.Empty : "\\";

        return
        [
            .. _session.GetChildren(parent)
                .Where(c => c.Kind == EntryKind.Folder)
                .Where(c => prefix.Length == 0 || c.Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                .Where(c => !string.Equals(basePath + separator + c.Name, typed, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSuggestions)
                .Select(c => basePath + separator + c.Name),
        ];
    }

    partial void OnIsMaskedChanged(bool value) => Refresh(_folder);

    partial void OnIsEditingChanged(bool value)
    {
        if (!value)
        {
            Refresh(_folder);
        }
    }
}
