using Bastion.App.Services;
using Bastion.App.Tests.Fakes;
using Bastion.App.ViewModels;
using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;
using NSubstitute;

namespace Bastion.App.Tests.EndToEnd;

/// <summary>
/// One test that walks the whole product: the real <see cref="VaultFactory"/> under the real
/// <see cref="ShellViewModel"/> and <see cref="ExplorerViewModel"/>, with only the dialogs and the OS
/// pickers substituted. Everything else - Argon2id, the save state machine, the index, AES-GCM, undo,
/// export, verify and the credential change - is the shipping code, working on a file in the temp
/// directory. The unit suites pin each part; this pins that the parts fit together.
/// </summary>
public sealed class RealVaultEndToEndTests : IDisposable
{
    private const string FirstPassword = "correct horse battery staple";
    private const string SecondPassword = "a different pass phrase entirely";

    /// <summary>8 MiB, one pass, one lane: legal per FORMAT.md section 7 and fast enough for a test.</summary>
    private static readonly KdfParameters TestKdf = new(8192, 1, 1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "BastionE2E", Guid.NewGuid().ToString("N"));
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IRecentVaults _recent = Substitute.For<IRecentVaults>();
    private readonly IRollbackGuard _rollback = Substitute.For<IRollbackGuard>();
    private readonly IAutoLockController _autoLock = Substitute.For<IAutoLockController>();
    private readonly IScreenPrivacy _privacy = Substitute.For<IScreenPrivacy>();
    private readonly ISingleInstance _singleInstance = Substitute.For<ISingleInstance>();
    private readonly IShellIntegration _integration = Substitute.For<IShellIntegration>();
    private readonly IKdfEstimator _estimator = Substitute.For<IKdfEstimator>();
    private readonly IOsClipboard _osClipboard = Substitute.For<IOsClipboard>();
    private readonly InternalClipboard _clipboard = new();
    private readonly MemorySettings _settings = new();
    private readonly MemoryLog _log = new();
    private readonly InlineDispatcher _dispatcher = new();

    private readonly string _vaultPath;
    private readonly string _sourceFolder;
    private readonly string _exportFolder;

    /// <summary>Lays out a temp directory with a small folder to import and a place to export to.</summary>
    public RealVaultEndToEndTests()
    {
        _vaultPath = Path.Combine(_root, "endtoend.bastion");
        _sourceFolder = Path.Combine(_root, "Papers");
        _exportFolder = Path.Combine(_root, "out");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_sourceFolder);
        Directory.CreateDirectory(_exportFolder);

        File.WriteAllText(Path.Combine(_sourceFolder, "notes.txt"), "the lamp is lit\r\n");
        File.WriteAllText(Path.Combine(_sourceFolder, "todo.md"), "# todo\r\n- ship it\r\n");
        File.WriteAllBytes(Path.Combine(_sourceFolder, "blob.bin"), Payload(300_000));

        _recent.Items.Returns([]);
        _singleInstance.TryAcquireVault(Arg.Any<string>()).Returns(_ => new DisposeFlag());
        _estimator.EstimateAsync(Arg.Any<KdfParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TimeSpan.FromMilliseconds(120)));
        _files.PickFolderToImport().Returns(_sourceFolder);
        _files.PickExportFolder().Returns(_exportFolder);
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Primary));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a run.
        }
    }

    [Fact]
    public async Task The_whole_product_creates_edits_saves_locks_rekeys_and_reopens_a_real_vault()
    {
        ShellViewModel shell = NewShell();

        // 1. Create the vault through the New vault dialog.
        StubDialogOnce(new NewVaultResult(_vaultPath, Passphrase.FromString(FirstPassword), null, TestKdf));

        await shell.NewVaultCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.True(File.Exists(_vaultPath), "creating a vault writes the file straight away");
        Assert.NotNull(shell.Explorer);
        Assert.NotNull(shell.Session);
        ExplorerViewModel explorer = shell.Explorer!;
        IVaultSession session = shell.Session!;
        Assert.False(session.IsDirty);
        Assert.Equal(1ul, session.Statistics.SaveCounter);

        // 2. Import the folder with its three files.
        await explorer.ImportFolderCommand.ExecuteAsync(null);

        Assert.True(session.IsDirty);
        EntryInfo folder = Assert.Single(session.GetChildren(EntryId.Root));
        Assert.Equal("Papers", folder.Name);
        Assert.Equal(EntryKind.Folder, folder.Kind);
        Assert.Equal(3, session.GetChildren(folder.Id).Count);
        Assert.Equal(4, session.Pending.Added);
        Assert.Single(explorer.Items);

        // 3. Save.
        await shell.SaveCommand.ExecuteAsync(null);

        Assert.False(session.IsDirty);
        Assert.Equal(2ul, session.Statistics.SaveCounter);
        Assert.All(session.GetChildren(folder.Id), entry => Assert.Equal(EntryState.Stored, entry.State));

        // 4. Lock, then unlock the same session with the real key derivation.
        await shell.LockCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.True(session.IsLocked);
        Assert.Null(shell.Explorer);
        _privacy.Received().SetExcludeFromCapture(false);

        using (Passphrase wrong = Passphrase.FromString("not the password"))
        {
            Assert.Equal(UnlockOutcome.WrongCredentials, await shell.Unlock.SubmitAsync(wrong, null));
        }

        Assert.True(session.IsLocked);

        using (Passphrase password = Passphrase.FromString(FirstPassword))
        {
            Assert.Equal(UnlockOutcome.Success, await shell.Unlock.SubmitAsync(password, null));
        }

        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.False(session.IsLocked);
        Assert.NotNull(shell.Explorer);
        explorer = shell.Explorer!;

        // 5. Rename a file, then undo the rename.
        explorer.NavigateTo(folder.Id);
        EntryItemViewModel row = explorer.Items.Single(i => i.RealName == "notes.txt");

        NameCheck check = await explorer.CommitRenameAsync(row, "readme.txt");
        Assert.True(check.IsValid, check.Reason);
        Assert.Contains(session.GetChildren(folder.Id), e => e.Name == "readme.txt");

        NameCheck refused = await explorer.CommitRenameAsync(
            explorer.Items.Single(i => i.RealName == "todo.md"), "readme.txt");
        Assert.False(refused.IsValid);
        Assert.NotNull(refused.Reason);

        Assert.True(session.CanUndo);
        await explorer.UndoCommand.ExecuteAsync(null);
        Assert.Contains(session.GetChildren(folder.Id), e => e.Name == "notes.txt");
        Assert.DoesNotContain(session.GetChildren(folder.Id), e => e.Name == "readme.txt");

        // 6. Export everything and compare the bytes with what went in.
        explorer.NavigateTo(EntryId.Root);
        explorer.SetSelection([]);
        await explorer.ExportCommand.ExecuteAsync(null);

        string exported = Path.Combine(_exportFolder, "Papers");
        Assert.True(Directory.Exists(exported), "the export writes the folder it was given");
        Assert.Equal("the lamp is lit\r\n", File.ReadAllText(Path.Combine(exported, "notes.txt")));
        Assert.Equal(Payload(300_000), File.ReadAllBytes(Path.Combine(exported, "blob.bin")));
        await _dialogs.Received().ConfirmAsync(
            Arg.Is<ConfirmRequest>(r => r.Detail != null && r.Detail.Contains("will create 3 files", StringComparison.Ordinal)));

        // 7. Verify.
        await shell.VerifyCommand.ExecuteAsync(null);

        Assert.NotNull(shell.LastVerifyReport);
        Assert.True(shell.LastVerifyReport!.IsClean);
        Assert.Equal(3, shell.LastVerifyReport.FilesChecked);
        Assert.False(shell.HasIntegrityFailure);

        // 8. Change the password with a full re-key, then save.
        StubDialogOnce(new ChangeCredentialsResult(
            Passphrase.FromString(FirstPassword),
            Passphrase.FromString(SecondPassword),
            null,
            TestKdf,
            CredentialChangeMode.Rekey));

        await shell.ChangeCredentialsCommand.ExecuteAsync(null);

        Assert.True(session.Pending.CredentialChangePending);
        Assert.True(session.Pending.RekeyPending);

        await shell.SaveCommand.ExecuteAsync(null);

        Assert.False(session.IsDirty);
        Assert.False(session.Pending.CredentialChangePending);

        // The old password is no longer the current one, so a second change is refused.
        StubDialogOnce(new ChangeCredentialsResult(
            Passphrase.FromString(FirstPassword),
            Passphrase.FromString("something else again"),
            null,
            TestKdf,
            CredentialChangeMode.RewrapOnly));

        await shell.ChangeCredentialsCommand.ExecuteAsync(null);
        Assert.False(session.Pending.CredentialChangePending);

        // 9. Reopen from disk with the new password, straight through Core.
        await shell.LockCommand.ExecuteAsync(null);
        await session.DisposeAsync();

        var factory = new VaultFactory();

        using (Passphrase stale = Passphrase.FromString(FirstPassword))
        {
            await Assert.ThrowsAsync<VaultAuthenticationException>(
                () => factory.OpenAsync(_vaultPath, stale, null, OpenOptions.Default, null, CancellationToken.None));
        }

        using Passphrase current = Passphrase.FromString(SecondPassword);
        await using IVaultSession reopened = await factory.OpenAsync(
            _vaultPath, current, null, OpenOptions.Default, null, CancellationToken.None);

        EntryInfo reopenedFolder = Assert.Single(reopened.GetChildren(EntryId.Root));
        Assert.Equal("Papers", reopenedFolder.Name);
        Assert.Equal(3, reopened.GetChildren(reopenedFolder.Id).Count);
        Assert.Contains(reopened.GetChildren(reopenedFolder.Id), e => e.Name == "notes.txt");
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);
        Assert.Equal(3ul, reopened.Statistics.SaveCounter);

        // Nothing temporary survived the run.
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(_root, "*.bak-*"));
        Assert.Empty(Directory.GetDirectories(_root, "*~stage-*"));
        Assert.DoesNotContain(_log.Lines, line => line.StartsWith("ERR", StringComparison.Ordinal));
    }

    private static byte[] Payload(int length)
    {
        byte[] bytes = new byte[length];
        new Random(4711).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Answers the next <see cref="IDialogService.ShowAsync{TResult}"/> for this result type with
    /// <paramref name="result"/>, and every later one with nothing. Change credentials re-shows its
    /// dialog until the current password is right, so a stub that never runs out would never return.
    /// </summary>
    /// <typeparam name="TResult">Result type of the dialog.</typeparam>
    /// <param name="result">What the user chose.</param>
    private void StubDialogOnce<TResult>(TResult result)
        where TResult : class
    {
        int calls = 0;
        _dialogs.ShowAsync(Arg.Any<DialogViewModelBase<TResult>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(calls++ == 0 ? result : null));
    }

    private ShellViewModel NewShell()
    {
        var operation = new OperationViewModel(_dispatcher, _log);

        return new ShellViewModel(
            new VaultFactory(),
            _dialogs,
            _files,
            _settings,
            _recent,
            _rollback,
            _clipboard,
            _autoLock,
            _privacy,
            _singleInstance,
            _integration,
            _estimator,
            _dispatcher,
            _log,
            operation,
            session => new ExplorerViewModel(
                session, _dialogs, _files, _clipboard, _osClipboard, _settings, _dispatcher, _log, operation));
    }
}
