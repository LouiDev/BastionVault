using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Tests.Explorer;

/// <summary>
/// A refresh that changes the shape of a folder replaces <see cref="ExplorerViewModel.Items"/>
/// wholesale, which swaps the list control's ItemsSource and clears the control's own selection
/// synchronously. The view model restores its selection from the new rows, but nothing used to
/// tell the control about it: the list showed nothing highlighted while Delete, Cut, Copy and
/// Export still acted on the invisible rows. These tests pin the event that closes that gap.
/// </summary>
public sealed class SelectionRestoredTests
{
    [Fact]
    public async Task AShapeChangingRefreshAnnouncesTheRestoredSelection()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        EntryItemViewModel first = explorer.Items[0];
        EntryItemViewModel second = explorer.Items[1];
        explorer.SetSelection([first, second]);

        IReadOnlyList<EntryItemViewModel>? announced = null;
        int raised = 0;
        explorer.SelectionRestored += (_, selection) =>
        {
            announced = selection;
            raised++;
        };

        // A folder arriving is the ordinary case: an import finishing, an undo, or a change
        // coming in over IVaultSession.Changed all land here.
        await context.Session.CreateFolderAsync(EntryId.Root, "Arrivals", CancellationToken.None);

        Assert.True(raised > 0, "a shape-changing refresh must announce the restored selection");
        Assert.NotNull(announced);
        Assert.Equal(2, explorer.SelectedItems.Count);
        Assert.Equal(
            [.. explorer.SelectedItems.Select(i => i.Id)],
            [.. announced!.Select(i => i.Id)]);

        // The restored rows are the new instances, not the ones the control has thrown away.
        Assert.All(announced, row => Assert.Contains(row, explorer.Items));
    }

    [Fact]
    public void ANewFolderAnnouncesItsOwnSelection()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;

        IReadOnlyList<EntryItemViewModel>? announced = null;
        explorer.SelectionRestored += (_, selection) => announced = selection;

        explorer.NewFolderCommand.Execute(null);

        Assert.NotNull(announced);
        Assert.Single(announced!);
        Assert.Equal("New folder", announced![0].RealName);
    }
}
