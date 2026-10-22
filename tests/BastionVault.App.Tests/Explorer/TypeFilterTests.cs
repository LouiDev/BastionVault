using BastionVault.App.Services;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The type filter narrows the listing and the search results to one family, hides folders unless
/// folders are what was asked for, and never confuses "this folder is empty" with "the filter hid
/// everything".
/// </summary>
public sealed class TypeFilterTests
{
    [Theory]
    [InlineData("Scan of ID card.png", FileTypeGroup.Images)]
    [InlineData("README.txt", FileTypeGroup.Documents)]
    [InlineData("notes.json", FileTypeGroup.Code)]
    [InlineData("song.mp3", FileTypeGroup.Audio)]
    [InlineData("clip.mp4", FileTypeGroup.Video)]
    [InlineData("backup.zip", FileTypeGroup.Archives)]
    [InlineData("signing.pfx", FileTypeGroup.Keys)]
    [InlineData("Wallet backup.dat", FileTypeGroup.Other)]
    [InlineData("mystery.xyz", FileTypeGroup.Other)]
    public void Every_type_belongs_to_one_family(string name, FileTypeGroup group)
    {
        Assert.Equal(group, FileTypeCatalog.Describe(name).Group);
    }

    [Fact]
    public void Every_group_has_a_label_and_all_types_comes_first()
    {
        FileTypeGroup[] groups = Enum.GetValues<FileTypeGroup>();

        Assert.Equal(FileTypeGroup.All, groups[0]);
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(FileTypeCatalog.GroupLabel(g))));
        Assert.Equal(groups.Length, groups.Select(FileTypeCatalog.GroupLabel).Distinct().Count());
    }

    [Theory]
    [InlineData(1500, true, true, true)]
    [InlineData(1360, true, true, true)]
    [InlineData(1300, true, true, false)]
    [InlineData(1180, true, true, false)]
    [InlineData(1100, false, true, true)]
    [InlineData(1000, false, true, false)]
    [InlineData(880, false, true, false)]
    [InlineData(600, false, false, false)]
    public void The_filter_is_drawn_only_where_the_bar_has_room_for_it(double width, bool labels, bool search, bool filter)
    {
        (bool l, bool s, bool f) = Views.CommandBarView.TierFor(width);

        Assert.Equal((labels, search, filter), (l, s, f));
    }

    [Fact]
    public void A_file_family_shows_only_those_files_and_no_folders()
    {
        using var context = new ExplorerTestContext();
        int all = context.Explorer.Items.Count;

        context.Explorer.TypeFilter = FileTypeGroup.Images;

        Assert.True(context.Explorer.IsTypeFilterActive);
        Assert.All(context.Explorer.Items, i => Assert.Equal(FileTypeGroup.Images, i.Group));
        Assert.Contains(context.Explorer.Items, i => i.RealName == "Scan of ID card.png");
        Assert.DoesNotContain(context.Explorer.Items, i => i.IsFolder);
        Assert.True(context.Explorer.Items.Count < all);
    }

    [Fact]
    public void Folders_shows_folders_alone_and_all_brings_everything_back()
    {
        using var context = new ExplorerTestContext();
        int all = context.Explorer.Items.Count;

        context.Explorer.TypeFilter = FileTypeGroup.Folders;
        Assert.NotEmpty(context.Explorer.Items);
        Assert.All(context.Explorer.Items, i => Assert.True(i.IsFolder));

        context.Explorer.ClearTypeFilterCommand.Execute(null);
        Assert.Equal(FileTypeGroup.All, context.Explorer.TypeFilter);
        Assert.Equal(all, context.Explorer.Items.Count);
    }

    [Fact]
    public void A_filter_that_hides_everything_is_not_an_empty_folder()
    {
        using var context = new ExplorerTestContext();

        context.Explorer.TypeFilter = FileTypeGroup.Video;

        Assert.Empty(context.Explorer.Items);
        Assert.True(context.Explorer.IsFilterEmpty);
        Assert.False(context.Explorer.IsFolderEmpty);
        Assert.False(context.Explorer.IsSearchEmpty);
    }

    [Fact]
    public void The_filter_drops_a_selection_it_hides()
    {
        using var context = new ExplorerTestContext();
        context.Select("README.txt", "Scan of ID card.png");

        context.Explorer.TypeFilter = FileTypeGroup.Images;

        EntryItemViewModel kept = Assert.Single(context.Explorer.SelectedItems);
        Assert.Equal("Scan of ID card.png", kept.RealName);
    }

    [Fact]
    public async Task The_filter_applies_to_search_results_too()
    {
        using var context = new ExplorerTestContext();
        context.Explorer.TypeFilter = FileTypeGroup.Documents;

        await context.SearchAsync("a", wholeVault: true);

        Assert.NotEmpty(context.Explorer.Items);
        Assert.All(context.Explorer.Items, i => Assert.Equal(FileTypeGroup.Documents, i.Group));
    }

    [Fact]
    public void Navigating_keeps_the_filter()
    {
        using var context = new ExplorerTestContext();
        context.Explorer.TypeFilter = FileTypeGroup.Documents;

        context.Explorer.NavigateTo(context.Explorer.Root.Children.First(c => c.Name == "Documents").Id);

        Assert.Equal(FileTypeGroup.Documents, context.Explorer.TypeFilter);
        Assert.All(context.Explorer.Items, i => Assert.Equal(FileTypeGroup.Documents, i.Group));
    }
}
