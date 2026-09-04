using BastionVault.App.Services;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The import flow: Core asks the app what to do about a name collision from its own thread, and
/// the app has to answer through the in-window dialog on the UI thread without deadlocking. It
/// also has to show the import report when, and only when, the import had something to report.
/// </summary>
public sealed class ImportFlowTests
{
    [Fact]
    public async Task ANameCollisionIsPutToTheUserThroughTheNameConflictDialog()
    {
        ConflictContext asked = Conflict("Notes.txt");
        IVaultSession session = EmptySession();
        ConflictDecision answered = ConflictDecision.Rename;

        session.ImportAsync(Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var options = call.Arg<ImportOptions>();
                answered = await options.ConflictResolver!(asked, CancellationToken.None).ConfigureAwait(false);
                return new ImportResult([], 0, []);
            });

        using var context = new ExplorerTestContext(session);
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<ConflictDecision>>(), Arg.Any<CancellationToken>())
            .Returns(ConflictDecision.ReplaceAll);

        await context.Explorer.ImportPathsAsync([@"C:\incoming\Notes.txt"]).ConfigureAwait(true);

        Assert.Equal(ConflictDecision.ReplaceAll, answered);
        await context.Dialogs.Received(1)
            .ShowAsync(Arg.Any<NameConflictDialogViewModel>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ADoThisForAllAnswerIsNotAskedTwice()
    {
        IVaultSession session = EmptySession();

        session.ImportAsync(Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var options = call.Arg<ImportOptions>();
                await options.ConflictResolver!(Conflict("a.txt"), CancellationToken.None).ConfigureAwait(false);
                await options.ConflictResolver!(Conflict("b.txt"), CancellationToken.None).ConfigureAwait(false);
                await options.ConflictResolver!(Conflict("c.txt"), CancellationToken.None).ConfigureAwait(false);
                return new ImportResult([], 0, []);
            });

        using var context = new ExplorerTestContext(session);
        context.Dialogs
            .ShowAsync(Arg.Any<DialogViewModelBase<ConflictDecision>>(), Arg.Any<CancellationToken>())
            .Returns(ConflictDecision.SkipAll);

        await context.Explorer.ImportPathsAsync([@"C:\incoming\a.txt", @"C:\incoming\b.txt", @"C:\incoming\c.txt"])
            .ConfigureAwait(true);

        await context.Dialogs.Received(1)
            .ShowAsync(Arg.Any<NameConflictDialogViewModel>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task TheImportReportIsShownOnlyWhenTheImportHadIssues()
    {
        IVaultSession clean = EmptySession();
        clean.ImportAsync(Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ImportResult([new EntryId(3)], 1024, [])));

        using (var context = new ExplorerTestContext(clean))
        {
            await context.Explorer.ImportPathsAsync([@"C:\incoming\one.txt"]).ConfigureAwait(true);

            await context.Dialogs.DidNotReceive()
                .ShowAsync(Arg.Any<ImportReportDialogViewModel>(), Arg.Any<CancellationToken>())
                .ConfigureAwait(true);
        }

        IVaultSession noisy = EmptySession();
        noisy.ImportAsync(Arg.Any<EntryId>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<ImportOptions>(),
                Arg.Any<IProgress<VaultProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ImportResult(
                [new EntryId(3)],
                1024,
                [new ImportIssue(@"C:\incoming\link", ImportIssueKind.SkippedReparsePoint, null)])));

        using (var context = new ExplorerTestContext(noisy))
        {
            await context.Explorer.ImportPathsAsync([@"C:\incoming\one.txt"]).ConfigureAwait(true);

            await context.Dialogs.Received(1)
                .ShowAsync(Arg.Any<ImportReportDialogViewModel>(), Arg.Any<CancellationToken>())
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ExportAsksBeforeItWritesAndSaysWhatItWillWrite()
    {
        using var context = new ExplorerTestContext();
        context.Files.PickExportFolder().Returns(@"C:\out");
        context.Dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(ConfirmResult.Cancel);

        context.Select("README.txt");
        await context.Explorer.ExportCommand.ExecuteAsync(null).ConfigureAwait(true);

        await context.Dialogs.Received(1).ConfirmAsync(Arg.Is<ConfirmRequest>(r =>
            r.PrimaryVerb == "Export"
            && r.Detail != null
            && r.Detail.Contains("will create 1 file", StringComparison.Ordinal)
            && r.Detail.Contains(@"C:\out", StringComparison.Ordinal))).ConfigureAwait(true);
    }

    [Fact]
    public async Task ExportDoesNothingWhenNoFolderIsPicked()
    {
        using var context = new ExplorerTestContext();
        context.Files.PickExportFolder().Returns((string?)null);

        context.Select("README.txt");
        await context.Explorer.ExportCommand.ExecuteAsync(null).ConfigureAwait(true);

        await context.Dialogs.DidNotReceive().ConfirmAsync(Arg.Any<ConfirmRequest>()).ConfigureAwait(true);
    }

    private static ConflictContext Conflict(string name) => new(
        EntryId.Root,
        name,
        new EntryInfo(new EntryId(2), EntryId.Root, EntryKind.File, name, 10, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, string.Empty, EntryState.Stored),
        @"C:\incoming\" + name,
        10);

    private static IVaultSession EmptySession()
    {
        IVaultSession session = Substitute.For<IVaultSession>();
        session.Path.Returns(@"C:\vaults\substitute.bastion");
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
