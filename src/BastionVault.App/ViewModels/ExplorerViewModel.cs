using System.Globalization;
using System.Windows.Input;
using BastionVault.App.Input;
using BastionVault.App.Services;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels;

/// <summary>
/// The explorer: the folder tree, the entry list, the address bar, the preview and everything a
/// user can do to the contents of an open vault. It owns no WPF type - navigation, selection,
/// sorting, search and every command are plain state here, and the views are a projection of it
/// (UI-CONTRACT.md section 1.1).
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject, IDisposable
{
    /// <summary>Search stops after this many hits; the list is a finding aid, not a report.</summary>
    private const int MaxSearchResults = 2000;

    /// <summary>How many source items an import pre-flight counts before it stops counting.</summary>
    private const int ImportCountCap = 20_000;

    private readonly IVaultSession _session;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;
    private readonly IInternalClipboard _clipboard;
    private readonly IOsClipboard _osClipboard;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILog _log;
    private readonly VaultChangeMarshaller _changes;

    private CancellationTokenSource? _search;
    private ConflictDecision? _conflictForAll;
    private int _conflictsRemaining;
    private bool _refreshScheduled;
    private bool _suppressSearchReload;
    private bool _suppressTreeSelection;
    private bool _disposed;

    [ObservableProperty]
    private EntryId _currentFolder = EntryId.Root;

    [ObservableProperty]
    private IReadOnlyList<EntryItemViewModel> _items = [];

    [ObservableProperty]
    private IReadOnlyList<EntryItemViewModel> _selectedItems = [];

    [ObservableProperty]
    private EntryItemViewModel? _focusedItem;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _searchWholeVault;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isSearchActive;

    [ObservableProperty]
    private EntrySortColumn _sortColumn = EntrySortColumn.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private RowDensity _density = RowDensity.Comfortable;

    [ObservableProperty]
    private bool _isPreviewVisible = true;

    [ObservableProperty]
    private bool _isPanicMode;

    [ObservableProperty]
    private bool _isWindowActive = true;

    /// <summary>Creates the explorer over an open session.</summary>
    /// <param name="session">The open vault session.</param>
    /// <param name="dialogs">In-window dialogs.</param>
    /// <param name="files">OS file pickers.</param>
    /// <param name="clipboard">The internal clipboard.</param>
    /// <param name="osClipboard">The OS clipboard, for path text and incoming file drops only.</param>
    /// <param name="settings">Application settings.</param>
    /// <param name="dispatcher">UI thread marshaller.</param>
    /// <param name="log">Log.</param>
    /// <param name="operation">The shared long-operation runner.</param>
    public ExplorerViewModel(
        IVaultSession session,
        IDialogService dialogs,
        IFileDialogService files,
        IInternalClipboard clipboard,
        IOsClipboard osClipboard,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        ILog log,
        OperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(operation);

        _session = session;
        _dialogs = dialogs;
        _files = files;
        _clipboard = clipboard;
        _osClipboard = osClipboard;
        _settings = settings;
        _dispatcher = dispatcher;
        _log = log;
        Operation = operation;

        _density = settings.Current.RowDensity;
        _isPreviewVisible = settings.Current.PreviewEnabled;
        _sortColumn = EntryComparer.ParseColumn(settings.Current.ColumnLayout.SortColumn);
        _sortAscending = settings.Current.ColumnLayout.SortAscending;

        History = new NavigationHistory();
        History.Changed += (_, _) => RaiseNavigationCanExecute();

        AddressBar = new AddressBarViewModel(session, id => NavigateTo(id));
        Preview = new PreviewViewModel(session, settings, log) { IsEnabled = _isPreviewVisible };
        StatusBar = new StatusBarViewModel(session, operation, UndoCommand);
        CommandBar = new CommandBarViewModel(this);

        Root = new FolderNodeViewModel(session, EntryId.Root, VaultName(session.Path), null, OnTreeNodeSelected);
        Root.IsExpanded = true;
        Tree = [Root];

        _changes = new VaultChangeMarshaller(dispatcher, _ => ScheduleRefresh());
        _changes.Attach(session);

        _clipboard.Changed += OnClipboardChanged;
        Operation.PropertyChanged += OnOperationChanged;

        ShortcutCommands = BuildShortcutCommands();

        History.Visit(EntryId.Root);
        RefreshNow();
    }

    /// <summary>Raised when the view should start the inline rename editor on a row.</summary>
    public event EventHandler<EntryItemViewModel>? RenameRequested;

    /// <summary>Raised when the view should put the caret in the search box.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>Raised when the view should move focus to the entry list.</summary>
    public event EventHandler? ListFocusRequested;

    /// <summary>Raised when the view should move focus on to the next (or previous) pane.</summary>
    public event EventHandler<bool>? FocusCycleRequested;

    /// <summary>Raised when the view should open the context menu at the focused row.</summary>
    public event EventHandler? ContextMenuRequested;

    /// <summary>Raised when the view should select every row.</summary>
    public event EventHandler? SelectAllRequested;

    /// <summary>
    /// Raised when the view model has re-established a selection the view cannot know about.
    /// Replacing <see cref="Items"/> swaps the list control's ItemsSource, which synchronously
    /// clears its own selection, so after a shape-changing refresh the restored rows have to be
    /// pushed back into the control or the list shows nothing highlighted while Delete, Cut,
    /// Copy and Export still act on the invisible selection.
    /// </summary>
    public event EventHandler<IReadOnlyList<EntryItemViewModel>>? SelectionRestored;

    /// <summary>The open session.</summary>
    public IVaultSession Session => _session;

    /// <summary>Application settings; the entry list persists its column layout through this.</summary>
    public ISettingsService Settings => _settings;

    /// <summary>The shared long-operation runner.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>Back and forward for the folder being shown.</summary>
    public NavigationHistory History { get; }

    /// <summary>The address bar.</summary>
    public AddressBarViewModel AddressBar { get; }

    /// <summary>The preview pane.</summary>
    public PreviewViewModel Preview { get; }

    /// <summary>The status bar.</summary>
    public StatusBarViewModel StatusBar { get; }

    /// <summary>The command bar.</summary>
    public CommandBarViewModel CommandBar { get; }

    /// <summary>The vault root node; the tree has exactly one.</summary>
    public FolderNodeViewModel Root { get; }

    /// <summary>Root nodes of the folder tree.</summary>
    public IReadOnlyList<FolderNodeViewModel> Tree { get; }

    /// <summary>Every Explorer-scope keymap action, by identifier, so the view can bind the lot.</summary>
    public IReadOnlyDictionary<string, ICommand> ShortcutCommands { get; }

    /// <summary>
    /// Records what a keymap gesture did, so "that shortcut does nothing" is answerable from the
    /// log file. Only the keymap id is written - never an entry name or an in-vault path
    /// (UI-CONTRACT.md section 1.13).
    /// </summary>
    /// <param name="id">Keymap id of the row that matched.</param>
    /// <param name="executed">Whether the command ran, or was refused by its CanExecute.</param>
    internal void LogShortcut(string id, bool executed) =>
        _log.Info($"Shortcut '{id}': {(executed ? "executed" : "refused, the command cannot run right now")}.");

    /// <summary>Path of the vault file.</summary>
    public string VaultPath => _session.Path;

    /// <summary>True while a long operation owns the vault; mutating commands are off.</summary>
    public bool IsBusy => Operation.IsRunning;

    /// <summary>
    /// How many entries the whole vault holds, folders included. It is the one number that is
    /// about the vault rather than about the folder on screen, and the shell reads it as a
    /// cheap proof that the session behind the explorer is the one it opened.
    /// </summary>
    public int ItemCount
    {
        get
        {
            VaultStatistics statistics = _session.Statistics;
            return statistics.FolderCount + statistics.FileCount;
        }
    }

    /// <summary>True when the list has nothing to show and no search is running.</summary>
    public bool IsFolderEmpty => Items.Count == 0 && !IsSearchActive && !IsSearching;

    /// <summary>True when a search returned nothing.</summary>
    public bool IsSearchEmpty => Items.Count == 0 && IsSearchActive && !IsSearching;

    /// <summary>Name of the folder being shown, for the empty state and the window title.</summary>
    public string CurrentFolderName =>
        CurrentFolder.IsRoot ? VaultName(_session.Path) : _session.Find(CurrentFolder)?.Name ?? VaultName(_session.Path);

    /// <summary>How long the search box waits after the last keystroke before it searches.</summary>
    internal TimeSpan SearchDebounce { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The search that is running, for tests to await.</summary>
    internal Task SearchCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Re-reads everything from the session. The shell calls this after every change it hears
    /// about; calls made in the same dispatcher turn are coalesced into one rebuild.
    /// </summary>
    public void Refresh() => ScheduleRefresh();

    /// <summary>Replaces the selection; the preview follows a single selected row.</summary>
    /// <param name="selection">The rows that are now selected.</param>
    public void SetSelection(IReadOnlyList<EntryItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        SelectedItems = selection;
        if (selection.Count > 0)
        {
            FocusedItem = selection[^1];
        }

        StatusBar.Update(Items, SelectedItems);
        Preview.Show(selection.Count == 1 ? selection[0] : null);
        RaiseSelectionCanExecute();
    }

    /// <summary>
    /// Asks the view to put keyboard focus on the entry list. The shell calls this when a vault
    /// opens: without it the caret stays on the window and every Explorer-scope shortcut is dead
    /// until the user clicks a row.
    /// </summary>
    public void FocusList() => ListFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Navigates to a folder and records the move in the history.</summary>
    /// <param name="folder">Folder to show.</param>
    public void NavigateTo(EntryId folder) => NavigateTo(folder, record: true);

    /// <summary>
    /// Renames an entry after validating the name through Core. The editor stays open when the
    /// name is refused, and the reason comes back so the view can show it.
    /// </summary>
    /// <param name="item">The row being renamed.</param>
    /// <param name="newName">What the user typed.</param>
    /// <returns>The verdict; <see cref="NameCheck.Ok"/> when the rename went through.</returns>
    public async Task<NameCheck> CommitRenameAsync(EntryItemViewModel item, string? newName)
    {
        ArgumentNullException.ThrowIfNull(item);

        string name = (newName ?? string.Empty).Trim();
        if (name.Length == 0 || string.Equals(name, item.RealName, StringComparison.Ordinal))
        {
            return NameCheck.Ok;
        }

        EntryId parent = item.Info.ParentId;
        NameCheck check = _session.ValidateName(parent, name, item.Id);
        if (!check.IsValid)
        {
            return check;
        }

        try
        {
            await _session.RenameAsync(item.Id, name, CancellationToken.None).ConfigureAwait(true);
            Refresh();
            return NameCheck.Ok;
        }
        catch (VaultException ex)
        {
            await ReportAsync("That name could not be used", ex).ConfigureAwait(true);
            return new NameCheck(false, ex.Message, check.Suggestion);
        }
    }

    /// <summary>
    /// Imports paths dropped from Explorer or pasted from the OS clipboard. The caller has already
    /// left the OLE drop loop (UI-CONTRACT.md section 1.7).
    /// </summary>
    /// <param name="paths">Full paths of the files and folders to import.</param>
    /// <param name="parent">Folder to import into; <see langword="null"/> for the current folder.</param>
    public async Task ImportPathsAsync(IReadOnlyList<string> paths, EntryId? parent = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0 || Operation.IsRunning)
        {
            return;
        }

        EntryId target = parent ?? CurrentFolder;
        _conflictForAll = null;
        _conflictsRemaining = CountImportItems(paths);

        var options = new ImportOptions(ConflictPolicy.Rename, ResolveConflictAsync);

        try
        {
            ImportResult? result = await Operation.RunAsync(
                VaultOperation.Import,
                paths.Count == 1 ? "Importing 1 item" : $"Importing {paths.Count} items",
                (progress, ct) => _session.ImportAsync(target, paths, options, progress, ct),
                isModal: false).ConfigureAwait(true);

            if (result is null)
            {
                StatusBar.Message = "Import cancelled.";
                return;
            }

            NavigateTo(target, record: false);
            StatusBar.Message = string.Create(
                CultureInfo.CurrentCulture,
                $"Imported {result.Imported.Count:N0} item{Plural(result.Imported.Count)} · {OperationViewModel.FormatBytes(result.BytesImported)}");

            if (result.Issues.Count > 0)
            {
                await _dialogs.ShowAsync(new ImportReportDialogViewModel(result)).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            await ReportAsync("The import did not finish", ex).ConfigureAwait(true);
        }
        finally
        {
            _conflictForAll = null;
            Refresh();
        }
    }

    /// <summary>
    /// Moves or copies entries into a folder, which is what an internal drag and drop does. A drop
    /// onto the dragged folder itself, or into one of its own descendants, is refused before Core
    /// is asked.
    /// </summary>
    /// <param name="ids">Entries being dragged.</param>
    /// <param name="target">Folder they are dropped on.</param>
    /// <param name="copy">True for a copy (Ctrl held), false for a move.</param>
    public async Task DropAsync(IReadOnlyList<EntryId> ids, EntryId target, bool copy)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0 || Operation.IsRunning)
        {
            return;
        }

        if (!CanDrop(ids, target))
        {
            StatusBar.Message = "A folder cannot be moved into itself.";
            return;
        }

        try
        {
            if (copy)
            {
                await _session.CopyAsync(ids, target, CancellationToken.None).ConfigureAwait(true);
                StatusBar.Message = $"Copied {ids.Count} item{Plural(ids.Count)}.";
            }
            else
            {
                await _session.MoveAsync(ids, target, CancellationToken.None).ConfigureAwait(true);
                StatusBar.Message = $"Moved {ids.Count} item{Plural(ids.Count)}.";
            }

            Refresh();
        }
        catch (VaultException ex)
        {
            await ReportAsync(copy ? "Those items could not be copied" : "Those items could not be moved", ex).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// True when <paramref name="ids"/> may be dropped on <paramref name="target"/>: no entry may
    /// land on itself, in the folder it already sits in, or inside one of its own descendants.
    /// </summary>
    /// <param name="ids">Entries being dragged.</param>
    /// <param name="target">Folder under the cursor.</param>
    public bool CanDrop(IReadOnlyList<EntryId> ids, EntryId target)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return false;
        }

        var dragged = new HashSet<EntryId>(ids);
        if (dragged.Contains(target))
        {
            return false;
        }

        foreach (EntryInfo ancestor in _session.GetAncestors(target))
        {
            if (dragged.Contains(ancestor.Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _clipboard.Changed -= OnClipboardChanged;
        Operation.PropertyChanged -= OnOperationChanged;
        _changes.Dispose();
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        Preview.Dispose();
        History.Clear();
        Items = [];
        SelectedItems = [];
        _log.Info("Explorer closed.");
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private static string VaultName(string path) =>
        string.IsNullOrEmpty(path) ? "Vault" : Path.GetFileNameWithoutExtension(path);

    private static string Join(string folderPath, string name) =>
        folderPath.EndsWith('\\') ? folderPath + name : folderPath + "\\" + name;

    // ── Navigation ────────────────────────────────────────────────────────────

    private void NavigateTo(EntryId folder, bool record)
    {
        if (!folder.IsRoot && _session.Find(folder) is not { Kind: EntryKind.Folder })
        {
            folder = EntryId.Root;
        }

        bool moved = folder != CurrentFolder;
        CurrentFolder = folder;

        if (record)
        {
            History.Visit(folder);
        }

        if (moved && IsSearchActive)
        {
            ClearSearchState();
        }

        SelectInTree(folder);
        AddressBar.Refresh(folder);
        LoadItems();
        SetSelection([]);
        OnPropertyChanged(nameof(CurrentFolderName));
        RaiseNavigationCanExecute();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (History.Back() is { } id)
        {
            NavigateTo(id, record: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward()
    {
        if (History.Forward() is { } id)
        {
            NavigateTo(id, record: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        EntryInfo? info = _session.Find(CurrentFolder);
        NavigateTo(info?.ParentId ?? EntryId.Root);
    }

    [RelayCommand]
    private void GoToRoot() => NavigateTo(EntryId.Root);

    private bool CanGoBack() => History.CanGoBack;

    private bool CanGoForward() => History.CanGoForward;

    private bool CanGoUp() => !CurrentFolder.IsRoot;

    private void RaiseNavigationCanExecute()
    {
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
    }

    private void SelectInTree(EntryId folder)
    {
        _suppressTreeSelection = true;
        try
        {
            FolderNodeViewModel? node = Root.FindLoaded(folder);
            if (node is null)
            {
                // Expand from the root down so the node exists before it is selected.
                Root.EnsureChildren();
                foreach (EntryInfo ancestor in _session.GetAncestors(folder))
                {
                    node = Root.FindLoaded(ancestor.Id);
                    node?.EnsureChildren();
                }

                node = Root.FindLoaded(folder);
            }

            if (node is null)
            {
                return;
            }

            node.ExpandAncestors();
            node.IsSelected = true;
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    private void OnTreeNodeSelected(FolderNodeViewModel node)
    {
        if (_suppressTreeSelection || _disposed)
        {
            return;
        }

        NavigateTo(node.Id);
    }

    // ── Listing, sorting, search ──────────────────────────────────────────────

    private void LoadItems()
    {
        string basePath = _session.FormatPath(CurrentFolder);
        var comparer = new EntryComparer(SortColumn, SortAscending);
        ClipboardOp? cut = _clipboard.Content is { IsCut: true } op ? op : null;

        List<EntryItemViewModel> items =
        [
            .. _session.GetChildren(CurrentFolder)
                .Select(info => new EntryItemViewModel(info, Join(basePath, info.Name))
                {
                    IsMasked = IsPanicMode,
                    IsCut = cut is not null && cut.Ids.Contains(info.Id),
                }),
        ];

        items.Sort(comparer);

        // When the folder still holds the same entries in the same order, update the rows that are
        // already on screen instead of replacing the list: swapping the source regenerates every
        // container, which would throw away the selection, the scroll offset and - the reason this
        // matters - the inline rename editor of a folder that was just created.
        if (SameShape(items))
        {
            for (int i = 0; i < items.Count; i++)
            {
                EntryItemViewModel row = Items[i];
                row.Update(items[i].Info);
                row.IsMasked = items[i].IsMasked;
                row.IsCut = items[i].IsCut;
            }
        }
        else
        {
            Items = items;
        }

        StatusBar.Update(Items, SelectedItems);
        RaiseEmptyState();
    }

    private bool SameShape(List<EntryItemViewModel> candidate)
    {
        if (Items.Count != candidate.Count)
        {
            return false;
        }

        for (int i = 0; i < candidate.Count; i++)
        {
            if (Items[i].Id != candidate[i].Id)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplySort()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var sorted = new List<EntryItemViewModel>(Items);
        sorted.Sort(new EntryComparer(SortColumn, SortAscending));
        Items = sorted;
    }

    partial void OnSortColumnChanged(EntrySortColumn value)
    {
        _settings.Current.ColumnLayout.SortColumn = EntryComparer.KeyOf(value);
        _settings.Save();
        ApplySort();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        _settings.Current.ColumnLayout.SortAscending = value;
        _settings.Save();
        ApplySort();
    }

    /// <summary>Sets the sort column, flipping the direction when the same column is picked again.</summary>
    /// <param name="column">Column key, as the persisted layout spells it.</param>
    [RelayCommand]
    private void SortBy(string? column)
    {
        EntrySortColumn next = EntryComparer.ParseColumn(column);
        if (next == SortColumn)
        {
            SortAscending = !SortAscending;
            return;
        }

        SortAscending = true;
        SortColumn = next;
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearchReload)
        {
            return;
        }

        _search?.Cancel();
        _search?.Dispose();
        _search = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            if (IsSearchActive)
            {
                ClearSearchState();
                LoadItems();
                SetSelection([]);
            }

            return;
        }

        var cancellation = new CancellationTokenSource();
        _search = cancellation;
        IsSearching = true;
        IsSearchActive = true;
        RaiseEmptyState();
        SearchCompletion = RunSearchAsync(value, cancellation.Token);
    }

    partial void OnSearchWholeVaultChanged(bool value)
    {
        if (IsSearchActive)
        {
            OnSearchTextChanged(SearchText);
        }
    }

    private async Task RunSearchAsync(string text, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDebounce, ct).ConfigureAwait(true);

            EntryId? scope = SearchWholeVault ? null : CurrentFolder;
            IReadOnlyList<EntryInfo> hits = await Task
                .Run(() => _session.Search(text, scope, MaxSearchResults, ct), ct)
                .ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            var comparer = new EntryComparer(SortColumn, SortAscending);
            List<EntryItemViewModel> items =
            [
                .. hits.Select(info => new EntryItemViewModel(info, _session.FormatPath(info.Id))
                {
                    IsMasked = IsPanicMode,
                }),
            ];

            items.Sort(comparer);
            Items = items;
            SetSelection([]);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is VaultException or NotImplementedException)
        {
            _log.Warn("The search failed.", ex);
            Items = [];
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsSearching = false;
                RaiseEmptyState();
            }
        }
    }

    private void ClearSearchState()
    {
        _search?.Cancel();
        _search?.Dispose();
        _search = null;
        IsSearching = false;
        IsSearchActive = false;

        if (SearchText.Length > 0)
        {
            _suppressSearchReload = true;
            try
            {
                SearchText = string.Empty;
            }
            finally
            {
                _suppressSearchReload = false;
            }
        }

        RaiseEmptyState();
    }

    /// <summary>Clears the search box and puts the folder listing back.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        if (!IsSearchActive && SearchText.Length == 0)
        {
            return;
        }

        ClearSearchState();
        LoadItems();
        SetSelection([]);
    }

    private void RaiseEmptyState()
    {
        OnPropertyChanged(nameof(IsFolderEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    // ── Opening, focus, view state ────────────────────────────────────────────

    /// <summary>Opens a row: a folder is navigated into, a file is previewed.</summary>
    /// <param name="target">The row, or <see langword="null"/> to use the focused one.</param>
    [RelayCommand]
    private void OpenEntry(object? target)
    {
        EntryItemViewModel? item = target as EntryItemViewModel
            ?? FocusedItem
            ?? (SelectedItems.Count == 1 ? SelectedItems[0] : null);

        if (item is null)
        {
            return;
        }

        if (item.IsFolder)
        {
            NavigateTo(item.Id);
            ListFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!IsPreviewVisible)
        {
            TogglePreview();
        }

        FocusedItem = item;
        Preview.Show(item);
    }

    /// <summary>Previews the focused row without opening anything.</summary>
    [RelayCommand]
    private void PreviewFocused()
    {
        if (!IsPreviewVisible)
        {
            TogglePreview();
        }

        Preview.Show(FocusedItem ?? (SelectedItems.Count == 1 ? SelectedItems[0] : null));
    }

    /// <summary>Shows or hides the preview pane and remembers the choice.</summary>
    [RelayCommand]
    private void TogglePreview()
    {
        IsPreviewVisible = !IsPreviewVisible;
        _settings.Current.PreviewEnabled = IsPreviewVisible;
        _settings.Save();
    }

    /// <summary>
    /// Panic (Ctrl+Shift+H): hides the preview and replaces every name with a mask, so a screen
    /// can be turned towards someone without closing the vault.
    /// </summary>
    [RelayCommand]
    private void Panic()
    {
        IsPanicMode = !IsPanicMode;
        StatusBar.Message = IsPanicMode ? "Names hidden. Ctrl+Shift+H shows them again." : null;
    }

    /// <summary>Sets the row height of the entry list and remembers it.</summary>
    /// <param name="density">Compact, Comfortable or Spacious, as text or as the enum.</param>
    [RelayCommand]
    private void SetDensity(object? density)
    {
        RowDensity value = density switch
        {
            RowDensity typed => typed,
            string text when Enum.TryParse(text, ignoreCase: true, out RowDensity parsed) => parsed,
            _ => Density,
        };

        Density = value;
        _settings.Current.RowDensity = value;
        _settings.Save();
    }

    /// <summary>Asks the view to put the caret in the search box.</summary>
    [RelayCommand]
    private void FocusSearch() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Switches the address bar to its editable path box.</summary>
    [RelayCommand]
    private void FocusAddressBar() => AddressBar.BeginEdit();

    /// <summary>Moves focus to the next pane, or the previous one when the parameter is truthy.</summary>
    /// <param name="backwards">True or "back" to cycle backwards.</param>
    [RelayCommand]
    private void CycleFocus(object? backwards) =>
        FocusCycleRequested?.Invoke(this, backwards is true or "back");

    /// <summary>Opens the context menu at the focused row.</summary>
    [RelayCommand]
    private void ShowContextMenu() => ContextMenuRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Selects every row in the list.</summary>
    [RelayCommand]
    private void SelectAll() => SelectAllRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Escape: leave the rename editor, the path box or the search, in that order.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        if (AddressBar.IsEditing)
        {
            AddressBar.CancelEdit();
            return;
        }

        if (IsSearchActive || SearchText.Length > 0)
        {
            ClearSearch();
        }
    }

    // ── Content commands ──────────────────────────────────────────────────────

    /// <summary>Creates "New folder" here and starts the inline rename on it.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task NewFolderAsync()
    {
        string name = UniqueName("New folder");

        try
        {
            EntryId id = await _session.CreateFolderAsync(CurrentFolder, name, CancellationToken.None).ConfigureAwait(true);
            RefreshNow();

            EntryItemViewModel? created = Items.FirstOrDefault(i => i.Id == id);
            if (created is null)
            {
                return;
            }

            SetSelection([created]);
            SelectionRestored?.Invoke(this, SelectedItems);

            // The change event has already queued a refresh; start the editor after it so the row
            // the editor attaches to is the one that survives.
            _dispatcher.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                EntryItemViewModel? row = Items.FirstOrDefault(i => i.Id == id);
                if (row is not null)
                {
                    RenameRequested?.Invoke(this, row);
                }
            });
        }
        catch (VaultException ex)
        {
            await ReportAsync("The folder could not be created", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Starts the inline rename editor on the focused row.</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        EntryItemViewModel? item = SelectedItems.Count == 1 ? SelectedItems[0] : FocusedItem;
        if (item is not null)
        {
            RenameRequested?.Invoke(this, item);
        }
    }

    /// <summary>Deletes the selection. There is no confirmation; Ctrl+Z puts it back.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        List<EntryId> ids = [.. SelectedItems.Select(i => i.Id)];
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await _session.DeleteAsync(ids, CancellationToken.None).ConfigureAwait(true);
            History.Prune(id => id.IsRoot || _session.Find(id) is not null);
            StatusBar.Message = $"Deleted {ids.Count} item{Plural(ids.Count)}. Ctrl+Z undoes it.";
            RefreshNow();
        }
        catch (VaultException ex)
        {
            await ReportAsync("Those items could not be deleted", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Puts the selection on the internal clipboard as a cut.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Cut() => SetClipboard(isCut: true);

    /// <summary>Puts the selection on the internal clipboard as a copy.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void Copy() => SetClipboard(isCut: false);

    private void SetClipboard(bool isCut)
    {
        List<EntryId> ids = [.. SelectedItems.Select(i => i.Id)];
        if (ids.Count == 0)
        {
            return;
        }

        _clipboard.Set(ids, isCut, _session.Path);
        StatusBar.Message = isCut
            ? $"Cut {ids.Count} item{Plural(ids.Count)}."
            : $"Copied {ids.Count} item{Plural(ids.Count)}.";
    }

    /// <summary>
    /// Pastes here. Files on the OS clipboard are imported; entries on the internal clipboard are
    /// moved or copied. Entries from another vault are refused - the ids mean nothing here.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (_osClipboard.HasFileDrop && _osClipboard.GetFileDropList() is { Count: > 0 } dropped)
        {
            await ImportPathsAsync(dropped).ConfigureAwait(true);
            return;
        }

        if (_clipboard.Content is not { } op)
        {
            return;
        }

        if (!string.Equals(op.SourceVaultPath, _session.Path, StringComparison.OrdinalIgnoreCase))
        {
            await _dialogs.ShowErrorAsync(
                "Those items are from another vault",
                "The internal clipboard holds entries of a different vault. Export them from that vault and import them here.")
                .ConfigureAwait(true);
            return;
        }

        List<EntryId> ids = [.. op.Ids.Where(id => _session.Find(id) is not null)];
        if (ids.Count == 0)
        {
            _clipboard.Clear();
            return;
        }

        await DropAsync(ids, CurrentFolder, copy: !op.IsCut).ConfigureAwait(true);

        if (op.IsCut)
        {
            _clipboard.Clear();
        }
    }

    /// <summary>Copies the in-vault path of the selection to the OS clipboard, as text only.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void CopyPath()
    {
        if (SelectedItems.Count == 0)
        {
            return;
        }

        string text = string.Join(Environment.NewLine, SelectedItems.Select(i => _session.FormatPath(i.Id)));
        _osClipboard.SetText(text);
        StatusBar.Message = SelectedItems.Count == 1 ? "Path copied." : $"{SelectedItems.Count} paths copied.";
    }

    /// <summary>Moves focus to the entry list; the tree's "Open" does exactly this.</summary>
    [RelayCommand]
    private void OpenTreeFolder() => ListFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Copies the in-vault path of the folder being shown.</summary>
    [RelayCommand]
    private void CopyTreePath()
    {
        _osClipboard.SetText(_session.FormatPath(CurrentFolder));
        StatusBar.Message = "Path copied.";
    }

    /// <summary>Picks files and imports them here.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ImportFilesAsync()
    {
        IReadOnlyList<string> picked = _files.PickFilesToImport();
        if (picked.Count > 0)
        {
            await ImportPathsAsync(picked).ConfigureAwait(true);
        }
    }

    /// <summary>Picks a folder and imports it here, with everything under it.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ImportFolderAsync()
    {
        if (_files.PickFolderToImport() is { Length: > 0 } folder)
        {
            await ImportPathsAsync([folder]).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Exports the selection, or the whole vault when nothing is selected. The user sees what the
    /// export will write - files and bytes - before a single file is created.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperation))]
    private async Task ExportAsync()
    {
        List<EntryId> ids = SelectedItems.Count > 0
            ? [.. SelectedItems.Select(i => i.Id)]
            : [.. _session.GetChildren(EntryId.Root).Select(c => c.Id)];

        if (ids.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Nothing to export", "This vault is empty.").ConfigureAwait(true);
            return;
        }

        (int files, long bytes) = Measure(ids);

        if (_files.PickExportFolder() is not { Length: > 0 } destination)
        {
            return;
        }

        ConfirmResult answer = await _dialogs.ConfirmAsync(new ConfirmRequest(
            SelectedItems.Count > 0 ? $"Export {ids.Count} item{Plural(ids.Count)}" : "Export everything",
            "Exported files are written in the clear. Anyone who can read that folder can read them.",
            PrimaryVerb: "Export",
            Detail: string.Create(
                CultureInfo.CurrentCulture,
                $"will create {files:N0} file{Plural(files)}, {OperationViewModel.FormatBytes(bytes)}\n{destination}")))
            .ConfigureAwait(true);

        if (answer != ConfirmResult.Primary)
        {
            return;
        }

        try
        {
            ExportResult? result = await Operation.RunAsync(
                VaultOperation.Export,
                $"Exporting {files:N0} file{Plural(files)}",
                (progress, ct) => _session.ExportAsync(ids, destination, new ExportOptions(), progress, ct),
                isModal: false).ConfigureAwait(true);

            if (result is null)
            {
                StatusBar.Message = "Export cancelled.";
                return;
            }

            StatusBar.Message = string.Create(
                CultureInfo.CurrentCulture,
                $"Exported {result.FilesWritten:N0} file{Plural(result.FilesWritten)} · {OperationViewModel.FormatBytes(result.BytesWritten)}");

            if (result.Issues.Count > 0)
            {
                await _dialogs.ShowErrorAsync(
                    "The export finished with problems",
                    $"{result.Issues.Count} item{Plural(result.Issues.Count)} could not be written as asked.",
                    string.Join(Environment.NewLine, result.Issues.Select(i => $"{i.VaultPath}: {i.Kind} {i.Detail}")))
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            await ReportAsync("The export did not finish", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Shows the properties of the selection, or of the vault when nothing is selected.</summary>
    [RelayCommand]
    private async Task PropertiesAsync()
    {
        PropertiesDialogViewModel dialog;

        if (SelectedItems.Count == 1)
        {
            EntryItemViewModel item = SelectedItems[0];
            dialog = new PropertiesDialogViewModel(item.Info, _session.FormatPath(item.Info.ParentId));
        }
        else
        {
            dialog = new PropertiesDialogViewModel(
                _session.Path,
                _session.Statistics,
                _session.Kdf,
                _session.Pending,
                _settings.Current.SizeObfuscation,
                _settings.Current.ReencryptOnSave);
        }

        await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
    }

    /// <summary>Undoes the last change to the tree.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!_session.CanUndo)
        {
            return;
        }

        string? what = _session.UndoDescription;

        try
        {
            await _session.UndoAsync(CancellationToken.None).ConfigureAwait(true);
            StatusBar.Message = what is null ? "Undone." : $"Undone: {what}";
            RefreshNow();
        }
        catch (VaultException ex)
        {
            await ReportAsync("That change could not be undone", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Redoes the change that was just undone.</summary>
    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (!_session.CanRedo)
        {
            return;
        }

        string? what = _session.RedoDescription;

        try
        {
            await _session.RedoAsync(CancellationToken.None).ConfigureAwait(true);
            StatusBar.Message = what is null ? "Redone." : $"Redone: {what}";
            RefreshNow();
        }
        catch (VaultException ex)
        {
            await ReportAsync("That change could not be redone", ex).ConfigureAwait(true);
        }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private bool CanMutate() => !Operation.IsRunning && !_session.IsReadOnly;

    private bool CanRunOperation() => !Operation.IsRunning;

    private bool CanActOnSelection() => SelectedItems.Count > 0 && CanMutate();

    private bool CanRename() => (SelectedItems.Count == 1 || FocusedItem is not null) && CanMutate();

    private bool CanPaste() => CanMutate() && (_clipboard.Content is not null || _osClipboard.HasFileDrop);

    private bool CanUndo() => _session.CanUndo && CanMutate();

    private bool CanRedo() => _session.CanRedo && CanMutate();

    private void RaiseSelectionCanExecute()
    {
        DeleteCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
    }

    private void RaiseAllCanExecute()
    {
        RaiseSelectionCanExecute();
        NewFolderCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        ImportFilesCommand.NotifyCanExecuteChanged();
        ImportFolderCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnOperationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(IsBusy));
            RaiseAllCanExecute();
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        ClipboardOp? content = _clipboard.Content;
        bool cut = content is { IsCut: true };

        foreach (EntryItemViewModel item in Items)
        {
            item.IsCut = cut && content!.Ids.Contains(item.Id);
        }

        PasteCommand.NotifyCanExecuteChanged();
    }

    private void ScheduleRefresh()
    {
        if (_refreshScheduled || _disposed)
        {
            return;
        }

        _refreshScheduled = true;
        _dispatcher.Post(() =>
        {
            _refreshScheduled = false;
            if (!_disposed)
            {
                RefreshNow();
            }
        });
    }

    private void RefreshNow()
    {
        if (!CurrentFolder.IsRoot && _session.Find(CurrentFolder) is null)
        {
            History.Prune(id => id.IsRoot || _session.Find(id) is not null);
            NavigateTo(History.Current ?? EntryId.Root, record: false);
            return;
        }

        Root.Refresh();
        AddressBar.Refresh(CurrentFolder);

        if (IsSearchActive)
        {
            StatusBar.RefreshVaultState();
            RaiseAllCanExecute();
            return;
        }

        IReadOnlyList<EntryId> keep = [.. SelectedItems.Select(i => i.Id)];
        LoadItems();

        List<EntryItemViewModel> restored = [.. Items.Where(i => keep.Contains(i.Id))];
        SelectedItems = restored;
        if (FocusedItem is not null)
        {
            FocusedItem = Items.FirstOrDefault(i => i.Id == FocusedItem.Id);
        }

        SelectionRestored?.Invoke(this, restored);

        StatusBar.Update(Items, SelectedItems);
        StatusBar.RefreshVaultState();
        OnPropertyChanged(nameof(CurrentFolderName));
        OnPropertyChanged(nameof(ItemCount));
        RaiseAllCanExecute();
        RaiseNavigationCanExecute();
    }

    private string UniqueName(string wanted)
    {
        if (_session.ValidateName(CurrentFolder, wanted).IsValid)
        {
            return wanted;
        }

        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{wanted} ({i})";
            if (_session.ValidateName(CurrentFolder, candidate).IsValid)
            {
                return candidate;
            }
        }

        return $"{wanted} {Guid.NewGuid():N}"[..32];
    }

    private (int Files, long Bytes) Measure(IReadOnlyList<EntryId> ids)
    {
        int files = 0;
        long bytes = 0;
        var stack = new Stack<EntryId>(ids);

        while (stack.Count > 0)
        {
            EntryId id = stack.Pop();
            if (_session.Find(id) is not { } info)
            {
                continue;
            }

            if (info.Kind == EntryKind.File)
            {
                files++;
                bytes += info.Length;
                continue;
            }

            foreach (EntryInfo child in _session.GetChildren(id))
            {
                stack.Push(child.Id);
            }
        }

        return (files, bytes);
    }

    private int CountImportItems(IReadOnlyList<string> paths)
    {
        int count = 0;

        foreach (string path in paths)
        {
            if (count >= ImportCountCap)
            {
                break;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    count += Directory
                        .EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                        .Take(ImportCountCap - count)
                        .Count();
                }
                else
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                count++;
            }
        }

        return Math.Max(count, paths.Count);
    }

    /// <summary>
    /// Answers an import name collision. Core calls this from its own thread, so the question is
    /// posted onto the UI thread and awaited; "do this for all" is remembered here so the dialog
    /// is not shown again for the rest of the import.
    /// </summary>
    /// <param name="context">Which name collided, and with what.</param>
    /// <param name="ct">Cancels the import.</param>
    private ValueTask<ConflictDecision> ResolveConflictAsync(ConflictContext context, CancellationToken ct)
    {
        if (_conflictForAll is { } remembered)
        {
            return ValueTask.FromResult(remembered);
        }

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromResult(ConflictDecision.Cancel);
        }

        var completion = new TaskCompletionSource<ConflictDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        int remaining = Math.Max(1, _conflictsRemaining);
        _conflictsRemaining = Math.Max(1, _conflictsRemaining - 1);

        _dispatcher.Post(() => _ = AskAsync());

        return new ValueTask<ConflictDecision>(completion.Task);

        async Task AskAsync()
        {
            try
            {
                var dialog = new NameConflictDialogViewModel(
                    context.Name,
                    _session.FormatPath(context.Parent),
                    remaining);

                ConflictDecision decision = await _dialogs.ShowAsync(dialog, ct).ConfigureAwait(true);

                if (decision is ConflictDecision.RenameAll or ConflictDecision.ReplaceAll or ConflictDecision.SkipAll)
                {
                    _conflictForAll = decision;
                }

                completion.TrySetResult(decision);
            }
            catch (Exception ex)
            {
                _log.Warn("A name conflict could not be presented.", ex);
                completion.TrySetResult(ConflictDecision.Rename);
            }
        }
    }

    private async Task ReportAsync(string title, Exception ex)
    {
        _log.Error(title, ex);
        await _dialogs.ShowErrorAsync(title, Explain(ex), ex.GetType().Name).ConfigureAwait(true);
    }

    private static string Explain(Exception ex) => ex switch
    {
        VaultOperationException { Code: VaultErrorCode.NameConflict } => "Something with that name is already here.",
        VaultOperationException { Code: VaultErrorCode.NameInvalid } => "That name is not allowed inside a vault.",
        VaultOperationException { Code: VaultErrorCode.InvalidMove } => "A folder cannot be moved into itself.",
        VaultOperationException { Code: VaultErrorCode.Busy } => "The vault is busy with another operation.",
        VaultOperationException { Code: VaultErrorCode.ReadOnlySession } => "This vault was opened read-only.",
        VaultIntegrityException => "The stored data failed its integrity check. Run Verify to see how much is affected.",
        VaultResourceException => "There is not enough room to finish this.",
        VaultIoException => "The file could not be read or written.",
        _ => ex.Message,
    };

    private Dictionary<string, ICommand> BuildShortcutCommands() => new(StringComparer.Ordinal)
    {
        ["ImportFiles"] = ImportFilesCommand,
        ["ImportFolder"] = ImportFolderCommand,
        ["Export"] = ExportCommand,
        ["NewFolder"] = NewFolderCommand,
        ["Rename"] = RenameCommand,
        ["Open"] = OpenEntryCommand,
        ["CancelEdit"] = CancelEditCommand,
        ["Delete"] = DeleteCommand,
        ["Undo"] = UndoCommand,
        ["Redo"] = RedoCommand,
        ["Cut"] = CutCommand,
        ["Copy"] = CopyCommand,
        ["Paste"] = PasteCommand,
        ["CopyPath"] = CopyPathCommand,
        ["SelectAll"] = SelectAllCommand,
        ["AddressBar"] = FocusAddressBarCommand,
        ["Back"] = BackCommand,
        ["Forward"] = ForwardCommand,
        ["Up"] = UpCommand,
        ["Root"] = GoToRootCommand,
        ["Search"] = FocusSearchCommand,
        ["Properties"] = PropertiesCommand,
        ["Preview"] = PreviewFocusedCommand,
        ["CycleFocus"] = CycleFocusCommand,
        ["ContextMenu"] = ShowContextMenuCommand,
        [KeyMap.Panic] = PanicCommand,
    };

    partial void OnIsPanicModeChanged(bool value)
    {
        foreach (EntryItemViewModel item in Items)
        {
            item.IsMasked = value;
        }

        Root.IsMasked = value;
        AddressBar.IsMasked = value;

        Preview.IsEnabled = IsPreviewVisible && !value;
        Preview.Show(value ? null : SelectedItems.Count == 1 ? SelectedItems[0] : null);
    }

    partial void OnIsPreviewVisibleChanged(bool value)
    {
        Preview.IsEnabled = value && !IsPanicMode;
        Preview.Show(value && !IsPanicMode && SelectedItems.Count == 1 ? SelectedItems[0] : null);
    }

    partial void OnIsWindowActiveChanged(bool value) => Preview.IsWindowActive = value;

    partial void OnItemsChanged(IReadOnlyList<EntryItemViewModel> value) => RaiseEmptyState();
}
