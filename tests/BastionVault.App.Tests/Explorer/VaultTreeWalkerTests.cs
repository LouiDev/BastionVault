using BastionVault.App.Services;
using BastionVault.App.Services.Demo;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The tree walk behind the Properties dialog's contents sections: it agrees with the session's
/// own statistics and ranks families and files by size.
/// </summary>
public sealed class VaultTreeWalkerTests
{
    [Fact]
    public void The_walk_sees_every_file_and_folder_the_statistics_count()
    {
        var session = new FakeVaultSession(@"C:\vaults\demo.bastion");
        VaultStatistics statistics = session.Statistics;

        VaultContentsSummary summary = VaultTreeWalker.Summarize(session);

        Assert.Equal(statistics.FileCount, summary.FileCount);
        Assert.Equal(statistics.FolderCount, summary.FolderCount);
        Assert.Equal(statistics.TotalPlaintextBytes, summary.TotalBytes);
        Assert.Equal(statistics.FileCount, VaultTreeWalker.EnumerateFiles(session, EntryId.Root).Count());
        Assert.Equal(summary.FileCount, summary.ByGroup.Sum(g => g.Count));
        Assert.Equal(summary.TotalBytes, summary.ByGroup.Sum(g => g.Bytes));
    }

    [Fact]
    public void Families_come_heaviest_first_and_the_largest_files_are_the_largest()
    {
        var session = new FakeVaultSession(@"C:\vaults\demo.bastion");

        VaultContentsSummary summary = VaultTreeWalker.Summarize(session);

        Assert.Equal(summary.ByGroup.OrderByDescending(g => g.Bytes).Select(g => g.Group), summary.ByGroup.Select(g => g.Group));
        Assert.DoesNotContain(summary.ByGroup, g => g.Group is FileTypeGroup.All or FileTypeGroup.Folders);

        Assert.Equal(VaultTreeWalker.LargestFilesKept, summary.LargestFiles.Count);
        long[] expected = [.. VaultTreeWalker.EnumerateFiles(session, EntryId.Root).Select(f => f.Info.Length).OrderByDescending(l => l).Take(VaultTreeWalker.LargestFilesKept)];
        Assert.Equal(expected, summary.LargestFiles.Select(f => f.Length));
        Assert.All(summary.LargestFiles, f => Assert.StartsWith(@"\", f.Path, StringComparison.Ordinal));
        Assert.True(summary.MaxDepth >= 2, "the demo vault nests a folder inside a folder");
    }

    [Fact]
    public void Depth_counts_folders_and_the_root_is_level_zero()
    {
        var session = new FakeVaultSession(@"C:\vaults\demo.bastion");

        WalkedFile deepest = VaultTreeWalker.EnumerateFiles(session, EntryId.Root).MaxBy(f => f.Depth)!;
        WalkedFile shallow = VaultTreeWalker.EnumerateFiles(session, EntryId.Root).First(f => f.Depth == 0);

        Assert.Equal(@"\", shallow.FolderPath);
        Assert.Equal(deepest.FolderPath.Count(c => c == '\\'), deepest.Depth);
    }

    [Fact]
    public void The_properties_dialog_shows_the_walk_as_three_sections()
    {
        var session = new FakeVaultSession(@"C:\vaults\demo.bastion");
        VaultContentsSummary summary = VaultTreeWalker.Summarize(session);

        var dialog = new PropertiesDialogViewModel(
            session.Path, session.Statistics, session.Kdf, session.Pending, false, false, summary);

        Assert.Equal(["File", "Contents", "By type", "Largest files", "Structure", "Key derivation", "History", "Write settings"], dialog.Sections.Select(s => s.Heading));
        PropertySection byType = dialog.Sections.Single(s => s.Heading == "By type");
        Assert.Equal(summary.ByGroup.Count, byType.Rows.Count);
        Assert.Contains(byType.Rows, r => r.Label == "Images" && r.Value.Contains("files ·", StringComparison.Ordinal));
        PropertySection largest = dialog.Sections.Single(s => s.Heading == "Largest files");
        Assert.All(largest.Rows, r => Assert.StartsWith(@"\", r.Value, StringComparison.Ordinal));

        var without = new PropertiesDialogViewModel(session.Path, session.Statistics, session.Kdf, session.Pending, false, false);
        Assert.DoesNotContain(without.Sections, s => s.Heading == "By type");
    }
}
