using System.Text;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// Duplicate detection: the scanner reads only files whose length occurs more than once and groups
/// those with the same digest; the dialog ticks and deletes; the explorer applies the decision.
/// </summary>
public sealed class DuplicatesTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static EntryInfo Entry(uint id, uint parent, string name, long length, EntryKind kind = EntryKind.File) =>
        new(new EntryId(id), new EntryId(parent), kind, name, length, 0, When, When, string.Empty, EntryState.Stored);

    /// <summary>
    /// A two-folder vault: the root holds a.txt, b.txt, c.txt, e.txt and the folder Sub; Sub holds d.txt.
    /// a, b and d say "hello", e says "hellp" (same length, different bytes), c is something else.
    /// </summary>
    private static (IVaultSession Session, List<EntryId> Opened) FakeVault()
    {
        var opened = new List<EntryId>();
        var contents = new Dictionary<uint, string>
        {
            [1] = "hello",
            [2] = "hello",
            [3] = "world!!",
            [4] = "hello",
            [5] = "hellp",
        };

        IVaultSession session = Substitute.For<IVaultSession>();
        session.GetChildren(EntryId.Root).Returns(
        [
            Entry(1, 0, "a.txt", 5),
            Entry(2, 0, "b.txt", 5),
            Entry(3, 0, "c.txt", 7),
            Entry(5, 0, "e.txt", 5),
            Entry(10, 0, "Sub", 5, EntryKind.Folder),
        ]);
        session.GetChildren(new EntryId(10)).Returns([Entry(4, 10, "d.txt", 5)]);
        session.FormatPath(EntryId.Root).Returns(@"\");
        session.FormatPath(new EntryId(10)).Returns(@"\Sub");
        session.OpenReadAsync(Arg.Any<EntryId>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.Arg<EntryId>();
            opened.Add(id);
            return Task.FromResult<Stream>(new MemoryStream(Encoding.ASCII.GetBytes(contents[id.Value])));
        });

        return (session, opened);
    }

    [Fact]
    public async Task Files_are_grouped_by_contents_and_only_length_collisions_are_read()
    {
        (IVaultSession session, List<EntryId> opened) = FakeVault();

        DuplicateScan scan = await DuplicateFinder.FindAsync(session, null, CancellationToken.None);

        Assert.Equal(5, scan.FilesScanned);
        DuplicateGroup group = Assert.Single(scan.Groups);
        Assert.Equal(5, group.Length);
        Assert.Equal([@"\a.txt", @"\b.txt", @"\Sub\d.txt"], group.Files.Select(f => f.Path));
        Assert.Equal(new EntryId(10), group.Files[2].ParentId);

        // c.txt is the only file of its length, so it was never decrypted.
        Assert.DoesNotContain(new EntryId(3), opened);
        Assert.Equal(4, opened.Count);
        Assert.Equal(20, scan.BytesHashed);
    }

    [Fact]
    public async Task Progress_is_reported_from_zero_to_the_bytes_that_had_to_be_read()
    {
        (IVaultSession session, _) = FakeVault();
        var reports = new List<VaultProgress>();
        var progress = Substitute.For<IProgress<VaultProgress>>();
        progress.When(p => p.Report(Arg.Any<VaultProgress>())).Do(call => reports.Add(call.Arg<VaultProgress>()));

        await DuplicateFinder.FindAsync(session, progress, CancellationToken.None);

        Assert.Equal(0, reports[0].BytesDone);
        Assert.Equal(20, reports[0].BytesTotal);
        Assert.Equal(20, reports[^1].BytesDone);
        Assert.Equal(4, reports[^1].ItemsDone);
        Assert.All(reports, r => Assert.True(r.IsCancellable));
    }

    [Fact]
    public async Task A_cancelled_scan_throws_cancellation()
    {
        (IVaultSession session, _) = FakeVault();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindAsync(session, null, cancellation.Token));
    }

    [Fact]
    public void The_dialog_sums_up_and_ticks_every_copy_but_the_first()
    {
        var groups = new List<DuplicateGroup>
        {
            new(1000, [new DuplicateFile(new EntryId(1), EntryId.Root, "/a"), new DuplicateFile(new EntryId(2), EntryId.Root, "/b"), new DuplicateFile(new EntryId(3), EntryId.Root, "/c")]),
            new(10, [new DuplicateFile(new EntryId(4), EntryId.Root, "/d"), new DuplicateFile(new EntryId(5), EntryId.Root, "/e")]),
        };
        var dialog = new DuplicatesDialogViewModel(groups, filesScanned: 42);

        Assert.Equal("2 groups of identical files", dialog.Title);
        Assert.Contains("42 files compared", dialog.Summary, StringComparison.Ordinal);
        Assert.Contains("3 extra copies", dialog.Summary, StringComparison.Ordinal);
        Assert.False(dialog.IsClean);
        Assert.Equal("Nothing ticked.", dialog.SelectionLine);
        Assert.False(dialog.DeleteSelectedCommand.CanExecute(null));

        dialog.KeepFirst();

        Assert.Equal(3, dialog.SelectedCount);
        Assert.Equal(2010, dialog.SelectedBytes);
        Assert.False(dialog.Groups[0].Rows[0].IsSelected);
        Assert.True(dialog.Groups[0].Rows[1].IsSelected);
        Assert.True(dialog.DeleteSelectedCommand.CanExecute(null));

        dialog.KeepAll();
        Assert.Equal(0, dialog.SelectedCount);
    }

    [Fact]
    public async Task Delete_ticked_closes_with_the_ticked_ids_and_show_closes_with_the_file()
    {
        var files = new List<DuplicateFile> { new(new EntryId(1), EntryId.Root, "/a"), new(new EntryId(2), new EntryId(9), "/Sub/b") };
        var dialog = new DuplicatesDialogViewModel([new DuplicateGroup(5, files)], 2);

        dialog.Groups[0].Rows[1].IsSelected = true;
        dialog.DeleteSelected();
        DeleteDuplicates deletion = Assert.IsType<DeleteDuplicates>(await dialog.Result);
        Assert.Equal([new EntryId(2)], deletion.Ids);

        var another = new DuplicatesDialogViewModel([new DuplicateGroup(5, files)], 2);
        another.Reveal(another.Groups[0].Rows[1]);
        RevealDuplicate reveal = Assert.IsType<RevealDuplicate>(await another.Result);
        Assert.Equal("/Sub/b", reveal.File.Path);
    }

    [Fact]
    public void A_clean_scan_says_so()
    {
        var dialog = new DuplicatesDialogViewModel([], filesScanned: 7);

        Assert.True(dialog.IsClean);
        Assert.Equal("No duplicates", dialog.Title);
        Assert.Contains("every one is different", dialog.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_explorer_runs_the_scan_and_deletes_what_the_dialog_ticked()
    {
        // The demo session serves zero bytes for every file, so every pair of equal-length files is a duplicate.
        using var context = new ExplorerTestContext();
        List<EntryId>? deleted = null;
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<DuplicatesDecision>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dialog = (DuplicatesDialogViewModel)call.Arg<DialogViewModelBase<DuplicatesDecision>>();
                Assert.False(dialog.IsClean, "the demo vault has files of equal length");
                dialog.KeepFirst();
                deleted = [.. dialog.Groups.SelectMany(g => g.Rows).Where(r => r.IsSelected).Select(r => r.File.Id)];
                dialog.DeleteSelected();
                return dialog.Result;
            });
        int before = context.Explorer.ItemCount;

        await context.Explorer.FindDuplicatesCommand.ExecuteAsync(null);

        Assert.NotNull(deleted);
        Assert.NotEmpty(deleted);
        Assert.Equal(before - deleted.Count, context.Explorer.ItemCount);
        Assert.True(context.Explorer.UndoCommand.CanExecute(null));
        Assert.Contains("Ctrl+Z", context.Explorer.StatusBar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_navigates_to_the_file_and_selects_it()
    {
        using var context = new ExplorerTestContext();
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<DuplicatesDecision>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dialog = (DuplicatesDialogViewModel)call.Arg<DialogViewModelBase<DuplicatesDecision>>();
                DuplicateRow nested = dialog.Groups.SelectMany(g => g.Rows).First(r => r.File.ParentId != EntryId.Root);
                dialog.Reveal(nested);
                return dialog.Result;
            });

        await context.Explorer.FindDuplicatesCommand.ExecuteAsync(null);

        Assert.NotEqual(EntryId.Root, context.Explorer.CurrentFolder);
        EntryItemViewModel selected = Assert.Single(context.Explorer.SelectedItems);
        Assert.StartsWith(context.Explorer.AddressBar.Path, selected.Path, StringComparison.Ordinal);
    }
}
