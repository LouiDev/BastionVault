using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The status line after an import or an export is a sentence a user reads, and a single-file
/// result used to report "Exported 1 files". Every other count in the same file already goes
/// through the Plural helper, so the inconsistency showed up inside one session.
/// </summary>
public sealed class ResultLinePluralTests
{
    [Fact]
    public async Task ImportingOneFileSaysOneItem()
    {
        IVaultSession session = EmptySession();
        session.ImportAsync(
                Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ImportResult([new EntryId(7)], 124, [])));

        using var context = new ExplorerTestContext(session);

        await context.Explorer.ImportPathsAsync([Path.Combine("incoming", "Notes.txt")]).ConfigureAwait(true);

        Assert.NotNull(context.Explorer.StatusBar.Message);
        Assert.StartsWith("Imported 1 item ", context.Explorer.StatusBar.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportingTwoFilesSaysTwoItems()
    {
        IVaultSession session = EmptySession();
        session.ImportAsync(
                Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ImportResult([new EntryId(7), new EntryId(8)], 248, [])));

        using var context = new ExplorerTestContext(session);

        await context.Explorer.ImportPathsAsync(
            [Path.Combine("incoming", "a.txt"), Path.Combine("incoming", "b.txt")]).ConfigureAwait(true);

        Assert.StartsWith("Imported 2 items ", context.Explorer.StatusBar.Message!, StringComparison.Ordinal);
    }

    private static IVaultSession EmptySession()
    {
        IVaultSession session = Substitute.For<IVaultSession>();
        session.Path.Returns(Path.Combine("vaults", "substitute.bastion"));
        session.Kdf.Returns(KdfParameters.Default);
        session.Pending.Returns(new PendingChanges(0, 0, 0, 0, false, false));
        session.Statistics.Returns(new VaultStatistics(0, 0, 0, 0, 1, DateTimeOffset.UnixEpoch, false));
        session.GetChildren(Arg.Any<EntryId>()).Returns([]);
        session.GetAncestors(Arg.Any<EntryId>()).Returns([]);
        session.FormatPath(Arg.Any<EntryId>()).Returns("\\");
        session.Find(Arg.Any<EntryId>()).Returns((EntryInfo?)null);
        return session;
    }
}
