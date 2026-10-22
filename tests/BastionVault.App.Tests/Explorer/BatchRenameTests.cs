using BastionVault.App.ViewModels;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// Batch rename: the name transform, the preview's verdicts, and the explorer path that opens the
/// dialog for a multi-selection and applies what it decided, one undoable rename at a time.
/// </summary>
public sealed class BatchRenameTests
{
    private static RenameCandidate File(uint id, string name) => new(new EntryId(id), EntryId.Root, name, IsFolder: false);

    private static RenameCandidate Folder(uint id, string name) => new(new EntryId(id), EntryId.Root, name, IsFolder: true);

    private static NameCheck AlwaysFine(RenameCandidate candidate, string name) => NameCheck.Ok;

    [Theory]
    [InlineData("IMG_0417.jpg", false, "IMG_", "Trip ", false, "", "", "Trip 0417.jpg")]
    [InlineData("IMG_0417.jpg", false, "img_", "", false, "", "", "0417.jpg")]
    [InlineData("IMG_0417.jpg", false, "img_", "x", true, "", "", "IMG_0417.jpg")]
    [InlineData("report.pdf", false, "", "", false, "2026 ", "", "2026 report.pdf")]
    [InlineData("report.pdf", false, "", "", false, "", " (final)", "report (final).pdf")]
    [InlineData("Contracts", true, "", "", false, "", " 2026", "Contracts 2026")]
    [InlineData(".gitignore", false, "", "", false, "", "-old", ".gitignore-old")]
    [InlineData("archive.tar.gz", false, "", "", false, "", "-1", "archive.tar-1.gz")]
    public void Transform_replaces_then_adds_prefix_and_suffix_before_the_extension(
        string name, bool isFolder, string find, string replace, bool matchCase, string prefix, string suffix, string expected)
    {
        Assert.Equal(expected, BatchRenameDialogViewModel.Transform(name, isFolder, find, replace, matchCase, prefix, suffix));
    }

    [Fact]
    public void The_preview_starts_unchanged_and_cannot_be_applied()
    {
        var dialog = new BatchRenameDialogViewModel([File(1, "a.txt"), File(2, "b.txt")], AlwaysFine);

        Assert.Equal("Rename 2 items", dialog.Title);
        Assert.All(dialog.Rows, r => Assert.False(r.IsChanged));
        Assert.False(dialog.CanApply);
        Assert.False(dialog.ApplyCommand.CanExecute(null));
        Assert.Contains("Nothing changes", dialog.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_updates_every_row_and_the_summary()
    {
        var dialog = new BatchRenameDialogViewModel([File(1, "a.txt"), File(2, "b.txt"), Folder(3, "Docs")], AlwaysFine);

        dialog.Prefix = "old-";

        Assert.Equal(["old-a.txt", "old-b.txt", "old-Docs"], dialog.Rows.Select(r => r.NewName));
        Assert.All(dialog.Rows, r => Assert.True(r.IsChanged));
        Assert.True(dialog.CanApply);
        Assert.Equal("3 of 3 items will be renamed.", dialog.Summary);
    }

    [Fact]
    public void Two_rows_that_would_collide_are_both_refused()
    {
        var dialog = new BatchRenameDialogViewModel([File(1, "photo-1.jpg"), File(2, "photo-2.jpg")], AlwaysFine);

        dialog.Find = "-1";
        dialog.Replace = "-2";

        BatchRenameRow changed = Assert.Single(dialog.Rows, r => r.IsChanged);
        Assert.Equal("photo-2.jpg", changed.NewName);
        Assert.False(dialog.CanApply, "renaming onto a name another selected row keeps must be refused");
    }

    [Fact]
    public void Two_rows_renamed_onto_the_same_new_name_are_both_refused()
    {
        var dialog = new BatchRenameDialogViewModel([File(1, "a-1.txt"), File(2, "a-2.txt")], AlwaysFine);

        // Dropping the digit sends both rows to "a-.txt".
        dialog.Find = "1";
        dialog.Replace = "";
        Assert.Equal("a-.txt", dialog.Rows[0].NewName);
        dialog.Find = "2";
        dialog.Replace = "1";
        Assert.Equal(["a-1.txt", "a-1.txt"], dialog.Rows.Select(r => r.NewName));

        BatchRenameRow changed = Assert.Single(dialog.Rows, r => r.IsChanged);
        Assert.Equal("Two items would get this name.", changed.Reason);
        Assert.False(dialog.CanApply);
    }

    [Fact]
    public void Cores_verdict_is_shown_on_the_row_and_blocks_apply()
    {
        var dialog = new BatchRenameDialogViewModel(
            [File(1, "a.txt"), File(2, "b.txt")],
            (candidate, name) => name.Contains('?') ? new NameCheck(false, "A name cannot contain ?.", null) : NameCheck.Ok);

        dialog.Suffix = "?";

        Assert.All(dialog.Rows, r => Assert.Equal("A name cannot contain ?.", r.Reason));
        Assert.All(dialog.Rows, r => Assert.True(r.IsInvalid));
        Assert.False(dialog.CanApply);
        Assert.Equal("2 new names cannot be used.", dialog.Summary);

        dialog.Suffix = "!";
        Assert.All(dialog.Rows, r => Assert.Null(r.Reason));
        Assert.True(dialog.CanApply);
    }

    [Fact]
    public async Task Apply_returns_only_the_rows_that_changed()
    {
        var dialog = new BatchRenameDialogViewModel([File(1, "keep.txt"), File(2, "old-name.txt")], AlwaysFine);
        dialog.Find = "old-";
        dialog.Replace = "new-";

        Assert.True(dialog.Accept());
        IReadOnlyList<RenameInstruction>? result = await dialog.Result;

        RenameInstruction only = Assert.Single(result!);
        Assert.Equal(new EntryId(2), only.Id);
        Assert.Equal("new-name.txt", only.NewName);
    }

    [Fact]
    public void A_single_selection_still_uses_the_inline_editor()
    {
        using var context = new ExplorerTestContext();
        EntryItemViewModel? requested = null;
        context.Explorer.RenameRequested += (_, item) => requested = item;
        context.Select("README.txt");

        context.Explorer.RenameCommand.Execute(null);

        Assert.NotNull(requested);
        Assert.Equal("README.txt", requested.RealName);
        context.Dialogs.DidNotReceiveWithAnyArgs().ShowAsync(Arg.Any<BatchRenameDialogViewModel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_multi_selection_opens_the_dialog_and_applies_its_renames()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt", "Licence keys.md");

        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<IReadOnlyList<RenameInstruction>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var dialog = (BatchRenameDialogViewModel)call.Arg<DialogViewModelBase<IReadOnlyList<RenameInstruction>>>();
                Assert.Equal(2, dialog.Rows.Count);
                dialog.Prefix = "2026 ";
                Assert.True(dialog.CanApply, "the demo vault must accept the prefixed names");
                dialog.Apply();
                return dialog.Result;
            });

        await context.Explorer.BatchRenameAsync();

        Assert.Contains(context.Explorer.Items, i => i.RealName == "2026 README.txt");
        Assert.Contains(context.Explorer.Items, i => i.RealName == "2026 Licence keys.md");
        Assert.DoesNotContain(context.Explorer.Items, i => i.RealName == "README.txt");
        Assert.True(context.Explorer.UndoCommand.CanExecute(null));
        Assert.Contains("Renamed 2 items", context.Explorer.StatusBar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_dialog_renames_nothing()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt", "Licence keys.md");
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<IReadOnlyList<RenameInstruction>>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RenameInstruction>?)null);

        await context.Explorer.BatchRenameAsync();

        Assert.Contains(context.Explorer.Items, i => i.RealName == "README.txt");
    }

    [Fact]
    public void The_dialog_asks_cores_name_check_with_the_candidate_excluded()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt", "Licence keys.md");
        BatchRenameDialogViewModel? shown = null;
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<IReadOnlyList<RenameInstruction>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                shown = (BatchRenameDialogViewModel)call.Arg<DialogViewModelBase<IReadOnlyList<RenameInstruction>>>();
                return Task.FromResult<IReadOnlyList<RenameInstruction>?>(null);
            });

        _ = context.Explorer.BatchRenameAsync();

        Assert.NotNull(shown);

        // A name that another entry in the folder already carries is refused by the fake session.
        shown.Find = "README.txt";
        shown.Replace = "Wallet backup.dat";
        BatchRenameRow refused = Assert.Single(shown.Rows, r => r.IsChanged);
        Assert.Contains("already used", refused.Reason, StringComparison.Ordinal);

        // Renaming an entry onto its own name, in a different case, is fine: it is excluded from the check.
        shown.Find = "README.txt";
        shown.Replace = "readme.txt";
        BatchRenameRow recased = Assert.Single(shown.Rows, r => r.IsChanged);
        Assert.Null(recased.Reason);
    }
}
