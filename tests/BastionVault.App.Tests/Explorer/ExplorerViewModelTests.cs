using BastionVault.App.Input;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The explorer, driven against the in-memory session the demo host uses. These tests are about
/// the promises the contract makes: navigation is a history, a delete is undoable, the clipboard
/// never leaves the vault, and no shortcut in the keymap is dead.
/// </summary>
public sealed class ExplorerViewModelTests
{
    [Fact]
    public void ItOpensOnTheRootWithFoldersFirst()
    {
        using var context = new ExplorerTestContext();

        Assert.Equal(EntryId.Root, context.Explorer.CurrentFolder);
        Assert.NotEmpty(context.Explorer.Items);

        int lastFolder = context.Explorer.Items.ToList().FindLastIndex(i => i.IsFolder);
        int firstFile = context.Explorer.Items.ToList().FindIndex(i => !i.IsFolder);
        Assert.True(lastFolder < firstFile, "every folder should come before every file");
    }

    [Fact]
    public void TheTreeStartsAtTheVaultRootAndExpands()
    {
        using var context = new ExplorerTestContext();

        FolderNodeViewModel root = context.Explorer.Root;
        Assert.True(root.IsRoot);
        Assert.Equal("demo", root.Name);
        Assert.True(root.IsExpanded);
        Assert.Contains(root.Children, c => c.Name == "Documents");
    }

    [Fact]
    public void OpeningAFolderNavigatesAndRecordsHistory()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.OpenEntryCommand.Execute(context.Item("Documents"));

        Assert.NotEqual(EntryId.Root, context.Explorer.CurrentFolder);
        Assert.Equal("Documents", context.Explorer.CurrentFolderName);
        Assert.True(context.Explorer.BackCommand.CanExecute(null));
        Assert.Contains(context.Explorer.Items, i => i.RealName == "Contracts");
    }

    [Fact]
    public void BackForwardUpAndRootWalkTheVault()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        explorer.OpenEntryCommand.Execute(context.Item("Documents"));
        EntryId documents = explorer.CurrentFolder;
        explorer.OpenEntryCommand.Execute(context.Item("Contracts"));

        explorer.BackCommand.Execute(null);
        Assert.Equal(documents, explorer.CurrentFolder);

        explorer.ForwardCommand.Execute(null);
        Assert.Equal("Contracts", explorer.CurrentFolderName);

        explorer.UpCommand.Execute(null);
        Assert.Equal(documents, explorer.CurrentFolder);

        explorer.GoToRootCommand.Execute(null);
        Assert.Equal(EntryId.Root, explorer.CurrentFolder);
        Assert.False(explorer.UpCommand.CanExecute(null));
    }

    [Fact]
    public void OpeningAFileShowsItInThePreviewInsteadOfNavigating()
    {
        using var context = new ExplorerTestContext();

        EntryItemViewModel file = context.Item("README.txt");
        context.Explorer.OpenEntryCommand.Execute(file);

        Assert.Equal(EntryId.Root, context.Explorer.CurrentFolder);
        Assert.Equal("README.txt", context.Explorer.Preview.Title);
    }

    [Fact]
    public void TheAddressBarBuildsCrumbsForTheCurrentFolder()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.OpenEntryCommand.Execute(context.Item("Documents"));
        context.Explorer.OpenEntryCommand.Execute(context.Item("Contracts"));

        Assert.Equal(["Vault", "Documents", "Contracts"], context.Explorer.AddressBar.Crumbs.Select(c => c.Name));
        Assert.True(context.Explorer.AddressBar.Crumbs[^1].IsLast);
        Assert.Equal(@"\Documents\Contracts", context.Explorer.AddressBar.Path);
    }

    [Fact]
    public void TheAddressBarNavigatesToATypedPath()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.AddressBar.BeginEdit();
        context.Explorer.AddressBar.EditText = @"\Photos\Trip 2025";

        Assert.True(context.Explorer.AddressBar.TryCommit());
        Assert.Equal("Trip 2025", context.Explorer.CurrentFolderName);
    }

    [Fact]
    public void TheAddressBarRefusesAPathThatDoesNotExist()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.AddressBar.BeginEdit();
        context.Explorer.AddressBar.EditText = @"\Nowhere\At all";

        Assert.False(context.Explorer.AddressBar.TryCommit());
        Assert.True(context.Explorer.AddressBar.HasError);
        Assert.True(context.Explorer.AddressBar.IsEditing);
    }

    [Fact]
    public void TheAddressBarCompletesFolderNames()
    {
        using var context = new ExplorerTestContext();

        IReadOnlyList<string> suggestions = context.Explorer.AddressBar.Complete(@"\Pho");

        Assert.Equal([@"\Photos"], suggestions);
    }

    [Fact]
    public void SortingByNameReversesTheFilesButKeepsFoldersFirst()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        List<string> ascending = [.. explorer.Items.Where(i => !i.IsFolder).Select(i => i.RealName)];
        explorer.SortByCommand.Execute("name");

        Assert.False(explorer.SortAscending);
        List<string> descending = [.. explorer.Items.Where(i => !i.IsFolder).Select(i => i.RealName)];
        ascending.Reverse();
        Assert.Equal(ascending, descending);
        Assert.True(explorer.Items[0].IsFolder);
    }

    [Fact]
    public void SortingBySizeOrdersTheFilesByBytes()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        explorer.SortByCommand.Execute("size");

        Assert.Equal(EntrySortColumn.Size, explorer.SortColumn);
        List<long> files = [.. explorer.Items.Where(i => !i.IsFolder).Select(i => i.Length)];
        Assert.Equal(files.OrderBy(l => l), files);
    }

    [Fact]
    public void TheSortIsPersisted()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.SortByCommand.Execute("modified");

        Assert.Equal("modified", context.Settings.Current.ColumnLayout.SortColumn);
        Assert.True(context.Settings.SaveCount > 0);
    }

    [Fact]
    public async Task SearchFindsMatchesAndClearingPutsTheFolderBack()
    {
        using var context = new ExplorerTestContext();
        int rootCount = context.Explorer.Items.Count;

        await context.SearchAsync("tax", wholeVault: true).ConfigureAwait(true);

        Assert.True(context.Explorer.IsSearchActive);
        Assert.NotEmpty(context.Explorer.Items);
        Assert.All(context.Explorer.Items, i => Assert.Contains("tax", i.RealName, StringComparison.OrdinalIgnoreCase));

        context.Explorer.ClearSearchCommand.Execute(null);

        Assert.False(context.Explorer.IsSearchActive);
        Assert.Equal(rootCount, context.Explorer.Items.Count);
    }

    [Fact]
    public async Task ASearchWithNoHitsRaisesTheEmptyState()
    {
        using var context = new ExplorerTestContext();

        await context.SearchAsync("no-such-thing-anywhere").ConfigureAwait(true);

        Assert.Empty(context.Explorer.Items);
        Assert.True(context.Explorer.IsSearchEmpty);
        Assert.False(context.Explorer.IsFolderEmpty);
    }

    [Fact]
    public async Task RenamingRefusesAnIllegalName()
    {
        using var context = new ExplorerTestContext();
        EntryItemViewModel item = context.Item("README.txt");

        NameCheck check = await context.Explorer.CommitRenameAsync(item, @"bad\name.txt").ConfigureAwait(true);

        Assert.False(check.IsValid);
        Assert.NotNull(check.Reason);
        Assert.Equal("README.txt", context.Item("README.txt").RealName);
    }

    [Fact]
    public async Task RenamingRefusesANameThatIsAlreadyUsed()
    {
        using var context = new ExplorerTestContext();
        EntryItemViewModel item = context.Item("README.txt");

        NameCheck check = await context.Explorer.CommitRenameAsync(item, "Licence keys.md").ConfigureAwait(true);

        Assert.False(check.IsValid);
        Assert.NotNull(check.Suggestion);
    }

    [Fact]
    public async Task RenamingAcceptsAGoodNameAndRelists()
    {
        using var context = new ExplorerTestContext();

        NameCheck check = await context.Explorer.CommitRenameAsync(context.Item("README.txt"), "Read me first.txt").ConfigureAwait(true);

        Assert.True(check.IsValid);
        Assert.Contains(context.Explorer.Items, i => i.RealName == "Read me first.txt");
        Assert.DoesNotContain(context.Explorer.Items, i => i.RealName == "README.txt");
    }

    [Fact]
    public async Task RenamingToTheSameNameIsANoOp()
    {
        using var context = new ExplorerTestContext();

        NameCheck check = await context.Explorer.CommitRenameAsync(context.Item("README.txt"), "README.txt").ConfigureAwait(true);

        Assert.True(check.IsValid);
        Assert.False(context.Session.CanUndo);
    }

    [Fact]
    public async Task NewFolderCreatesAUniqueNameAndAsksForARename()
    {
        using var context = new ExplorerTestContext();
        EntryItemViewModel? renaming = null;
        context.Explorer.RenameRequested += (_, item) => renaming = item;

        await context.Explorer.NewFolderCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Contains(context.Explorer.Items, i => i.RealName == "New folder");
        Assert.NotNull(renaming);
        Assert.Equal("New folder", renaming!.RealName);

        await context.Explorer.NewFolderCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Contains(context.Explorer.Items, i => i.RealName == "New folder (2)");
    }

    [Fact]
    public async Task DeleteRemovesTheSelectionAndUndoPutsItBack()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt", "Licence keys.md");

        await context.Explorer.DeleteCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.DoesNotContain(context.Explorer.Items, i => i.RealName == "README.txt");
        Assert.DoesNotContain(context.Explorer.Items, i => i.RealName == "Licence keys.md");
        Assert.True(context.Explorer.UndoCommand.CanExecute(null));

        await context.Explorer.UndoCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Contains(context.Explorer.Items, i => i.RealName == "README.txt");
        Assert.Contains(context.Explorer.Items, i => i.RealName == "Licence keys.md");
    }

    [Fact]
    public async Task CutAndPasteMovesInsideTheVault()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        context.Select("README.txt");
        explorer.CutCommand.Execute(null);
        Assert.True(context.Clipboard.Content is { IsCut: true });

        explorer.OpenEntryCommand.Execute(context.Item("Documents"));
        await explorer.PasteCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Contains(explorer.Items, i => i.RealName == "README.txt");
        Assert.Null(context.Clipboard.Content);

        explorer.GoToRootCommand.Execute(null);
        Assert.DoesNotContain(explorer.Items, i => i.RealName == "README.txt");
    }

    [Fact]
    public async Task CopyAndPasteLeavesTheOriginalWhereItIs()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        context.Select("README.txt");
        explorer.CopyCommand.Execute(null);

        explorer.OpenEntryCommand.Execute(context.Item("Documents"));
        await explorer.PasteCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Contains(explorer.Items, i => i.RealName.StartsWith("README", StringComparison.Ordinal));
        Assert.NotNull(context.Clipboard.Content);

        explorer.GoToRootCommand.Execute(null);
        Assert.Contains(explorer.Items, i => i.RealName == "README.txt");
    }

    [Fact]
    public async Task PastingEntriesFromAnotherVaultIsRefused()
    {
        using var context = new ExplorerTestContext();
        context.Clipboard.Set([new EntryId(1)], isCut: false, @"C:\vaults\other.bastion");

        await context.Explorer.PasteCommand.ExecuteAsync(null).ConfigureAwait(true);

        await context.Dialogs.Received(1).ShowErrorAsync(
            Arg.Is<string>(t => t.Contains("another vault", StringComparison.Ordinal)),
            Arg.Any<string>(),
            Arg.Any<string?>()).ConfigureAwait(true);
    }

    [Fact]
    public async Task PastingFilesFromTheOsClipboardImportsThem()
    {
        using var context = new ExplorerTestContext();
        context.OsClipboard.FileDrop = [@"C:\incoming\one.txt", @"C:\incoming\two.txt"];

        await context.Explorer.PasteCommand.ExecuteAsync(null).ConfigureAwait(true);

        Assert.Contains(context.Explorer.Items, i => i.RealName == "one.txt");
        Assert.Contains(context.Explorer.Items, i => i.RealName == "two.txt");
    }

    [Fact]
    public void CopyPathWritesOnlyTextToTheOsClipboard()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt");

        context.Explorer.CopyPathCommand.Execute(null);

        Assert.Equal([@"\README.txt"], context.OsClipboard.Written);
    }

    [Fact]
    public void DroppingAFolderIntoItselfIsRefused()
    {
        using var context = new ExplorerTestContext();
        EntryItemViewModel documents = context.Item("Documents");
        context.Explorer.OpenEntryCommand.Execute(documents);
        EntryId contracts = context.Item("Contracts").Id;

        Assert.False(context.Explorer.CanDrop([documents.Id], contracts));
        Assert.False(context.Explorer.CanDrop([documents.Id], documents.Id));
        Assert.True(context.Explorer.CanDrop([contracts], EntryId.Root));
    }

    [Fact]
    public async Task DroppingOntoAFolderMovesTheEntries()
    {
        using var context = new ExplorerTestContext();
        EntryId photos = context.Item("Photos").Id;
        EntryId readme = context.Item("README.txt").Id;

        await context.Explorer.DropAsync([readme], photos, copy: false).ConfigureAwait(true);

        Assert.DoesNotContain(context.Explorer.Items, i => i.Id == readme);
        Assert.Equal(photos, context.Session.Find(readme)!.ParentId);
    }

    [Fact]
    public void PanicHidesThePreviewAndMasksNames()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt");

        context.Explorer.PanicCommand.Execute(null);

        Assert.True(context.Explorer.IsPanicMode);
        Assert.All(context.Explorer.Items, i => Assert.DoesNotContain(".txt", i.Name, StringComparison.Ordinal));
        Assert.Equal(PreviewMode.Hidden, context.Explorer.Preview.Mode);

        context.Explorer.PanicCommand.Execute(null);

        Assert.False(context.Explorer.IsPanicMode);
        Assert.Contains(context.Explorer.Items, i => i.Name.EndsWith(".txt", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDensityChoiceIsRemembered()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.SetDensityCommand.Execute("Compact");

        Assert.Equal(RowDensity.Compact, context.Explorer.Density);
        Assert.Equal(RowDensity.Compact, context.Settings.Current.RowDensity);
    }

    [Fact]
    public void TheStatusBarCountsTheFolderAndTheSelection()
    {
        using var context = new ExplorerTestContext();
        StatusBarViewModel status = context.Explorer.StatusBar;

        Assert.False(status.HasSelection);
        Assert.Contains("items", status.ItemsLine, StringComparison.Ordinal);
        Assert.Contains("Argon2id", status.KdfLine, StringComparison.Ordinal);

        context.Select("README.txt", "Licence keys.md");

        Assert.True(status.HasSelection);
        Assert.Contains("2 of", status.SelectionLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePendingChipReportsWhatASaveWouldCommit()
    {
        using var context = new ExplorerTestContext();

        Assert.True(context.Explorer.StatusBar.HasPending);
        Assert.Contains("added", context.Explorer.StatusBar.PendingLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTreeShowsAPendingDotOnFoldersWithPendingDescendants()
    {
        using var context = new ExplorerTestContext();

        FolderNodeViewModel documents = context.Explorer.Root.Children.First(c => c.Name == "Documents");
        FolderNodeViewModel keys = context.Explorer.Root.Children.First(c => c.Name == "Keys and certificates");

        Assert.NotEqual(BastionVault.App.Controls.PipState.None, documents.Pip);
        Assert.NotEqual(BastionVault.App.Controls.PipState.None, keys.Pip);
    }

    [Fact]
    public void EveryExplorerShortcutHasACommandBehindIt()
    {
        using var context = new ExplorerTestContext();

        List<string> dead =
        [
            .. KeyMap.Entries
                .Where(e => e.Scope == ShortcutScope.Explorer)
                .Select(e => e.Id)
                .Where(id => !context.Explorer.ShortcutCommands.ContainsKey(id)),
        ];

        Assert.Empty(dead);
        Assert.True(context.Explorer.ShortcutCommands.ContainsKey(KeyMap.Panic), "Panic must be bound by the explorer");
    }

    [Fact]
    public void EveryCommandBarButtonCarriesItsShortcut()
    {
        using var context = new ExplorerTestContext();

        foreach (CommandBarButton button in context.Explorer.CommandBar.Groups.SelectMany(g => g.Buttons))
        {
            Assert.StartsWith(button.Label, button.ToolTip, StringComparison.Ordinal);
            Assert.Contains("(", button.ToolTip, StringComparison.Ordinal);
            Assert.NotNull(button.Command);
        }
    }

    [Fact]
    public void DisposingClearsTheHistoryAndTheListing()
    {
        var context = new ExplorerTestContext();
        context.Explorer.OpenEntryCommand.Execute(context.Item("Documents"));

        context.Explorer.Dispose();

        Assert.Empty(context.Explorer.Items);
        Assert.Empty(context.Explorer.History.Places);
    }
}
