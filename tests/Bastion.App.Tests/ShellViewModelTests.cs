using Bastion.App.Services;
using Bastion.App.Services.Demo;
using Bastion.App.Tests.Fakes;
using Bastion.App.ViewModels;
using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;
using NSubstitute;

namespace Bastion.App.Tests;

/// <summary>
/// The shell state machine (UI-CONTRACT.md section 4): NoVault - Locked - Unlocking - Open -
/// Locked. The transitions are what every other part of the window keys off, so they are pinned
/// end to end against the in-memory session rather than mocked one call at a time.
/// </summary>
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "BastionTests", Guid.NewGuid().ToString("N") + ".bastion");

    private readonly IVaultFactory _factory = Substitute.For<IVaultFactory>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IRecentVaults _recent = Substitute.For<IRecentVaults>();
    private readonly IRollbackGuard _rollback = Substitute.For<IRollbackGuard>();
    private readonly IAutoLockController _autoLock = Substitute.For<IAutoLockController>();
    private readonly IScreenPrivacy _privacy = Substitute.For<IScreenPrivacy>();
    private readonly ISingleInstance _singleInstance = Substitute.For<ISingleInstance>();
    private readonly IShellIntegration _shellIntegration = Substitute.For<IShellIntegration>();
    private readonly IKdfEstimator _estimator = Substitute.For<IKdfEstimator>();
    private readonly IOsClipboard _osClipboard = Substitute.For<IOsClipboard>();
    private readonly InternalClipboard _clipboard = new();
    private readonly MemorySettings _settings = new();
    private readonly MemoryLog _log = new();
    private readonly InlineDispatcher _dispatcher = new();

    private FakeVaultSession _session;

    /// <summary>Sets up a shell whose factory hands out an in-memory session.</summary>
    public ShellViewModelTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_vaultPath)!);
        File.WriteAllBytes(_vaultPath, VaultHeaderBytes());

        _session = new FakeVaultSession(_vaultPath);

        _files.PickVaultToOpen().Returns(_vaultPath);
        _singleInstance.TryAcquireVault(Arg.Any<string>()).Returns(_ => new DisposeFlag());
        _recent.Items.Returns([]);
        _factory.ReadHeaderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new VaultHeaderInfo(1, KdfParameters.Default, 4096, 64, 512L * 1024 * 1024)));
        _factory.OpenAsync(
                Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<OpenOptions>(),
                Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IVaultSession>(_session));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (File.Exists(_vaultPath))
        {
            File.Delete(_vaultPath);
        }
    }

    [Fact]
    public void AFreshShellHasNoVault()
    {
        ShellViewModel shell = NewShell();

        Assert.Equal(ShellMode.NoVault, shell.Mode);
        Assert.Null(shell.Session);
        Assert.Null(shell.Explorer);
        Assert.True(shell.IsStartVisible);
        Assert.False(shell.IsUnlockVisible);
        Assert.False(shell.IsExplorerVisible);
        Assert.Equal(StripeState.None, shell.Stripe);
        Assert.Equal("Bastion", shell.Title);
    }

    [Fact]
    public async Task PickingAVaultShowsTheUnlockCardBeforeAnyKeyIsDerived()
    {
        ShellViewModel shell = NewShell();

        await shell.OpenVaultCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.True(shell.IsUnlockVisible);
        Assert.NotNull(shell.Unlock.UnlockRequested);
        Assert.Contains("512 MiB", shell.Unlock.HeaderLine, StringComparison.Ordinal);
        Assert.Contains("Deriving key", shell.Unlock.DerivingLabel, StringComparison.Ordinal);
        Assert.Equal(StripeState.Locked, shell.Stripe);

        // Nothing was opened yet: the KDF only runs when the user submits.
        await _factory.DidNotReceive().OpenAsync(
            Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<OpenOptions>(),
            Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnlockingOpensTheExplorer()
    {
        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);

        UnlockOutcome outcome = await shell.Unlock.SubmitAsync(null, null);

        Assert.Equal(UnlockOutcome.Success, outcome);
        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.Same(_session, shell.Session);
        Assert.NotNull(shell.Explorer);
        Assert.True(shell.IsExplorerVisible);
        Assert.Equal(42, shell.Explorer!.ItemCount);
        _recent.Received().Touch(_vaultPath);
        _privacy.Received().SetExcludeFromCapture(true);
    }

    [Fact]
    public async Task AWrongPasswordKeepsTheCardAndCountsTheFailure()
    {
        _factory.OpenAsync(
                Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<OpenOptions>(),
                Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IVaultSession>>(_ => throw new VaultAuthenticationException(
                VaultErrorCode.AuthenticationFailed, "no"));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);

        UnlockOutcome outcome = await shell.Unlock.SubmitAsync(null, null);

        Assert.Equal(UnlockOutcome.WrongCredentials, outcome);
        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.Equal(1, shell.Unlock.FailureCount);
        Assert.True(shell.Unlock.HasError);
        Assert.Null(shell.Session);
    }

    [Fact]
    public async Task ADamagedVaultGetsItsOwnMessage()
    {
        _factory.OpenAsync(
                Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<OpenOptions>(),
                Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IVaultSession>>(_ => throw new VaultFormatException(
                VaultErrorCode.IndexCorrupt, "altered"));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);

        UnlockOutcome outcome = await shell.Unlock.SubmitAsync(null, null);

        Assert.Equal(UnlockOutcome.Damaged, outcome);
        Assert.Contains("damaged", shell.Unlock.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LockingWithUnsavedWorkAsksFirstAndThenGoesBackToTheCard()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Secondary));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);
        Assert.True(shell.IsDirty);

        await shell.LockCommand.ExecuteAsync(null);

        await _dialogs.Received().ConfirmAsync(Arg.Is<ConfirmRequest>(r => r.Title.StartsWith("Lock with", StringComparison.Ordinal)));
        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.Null(shell.Explorer);
        Assert.True(_session.IsLocked);
        _privacy.Received().SetExcludeFromCapture(false);
    }

    [Fact]
    public async Task CancellingTheLockPromptLeavesTheVaultOpen()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Cancel));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        await shell.LockCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.False(_session.IsLocked);
    }

    [Fact]
    public async Task UnlockingAgainReturnsToTheExplorerWithTheSameSession()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Secondary));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);
        await shell.LockCommand.ExecuteAsync(null);

        UnlockOutcome outcome = await shell.Unlock.SubmitAsync(null, null);

        Assert.Equal(UnlockOutcome.Success, outcome);
        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.Same(_session, shell.Session);
        Assert.False(_session.IsLocked);
    }

    [Fact]
    public async Task SavingClearsTheDirtyFlagAndTheStripe()
    {
        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);
        Assert.Equal(StripeState.Unsaved, shell.Stripe);

        await shell.SaveCommand.ExecuteAsync(null);

        Assert.False(shell.IsDirty);
        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.Equal(StripeState.Saved, shell.Stripe);
        _rollback.Received().Record(Arg.Any<string>(), Arg.Any<ulong>());
    }

    [Fact]
    public async Task ClosingWithUnsavedWorkCanBeCancelled()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Cancel));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        Assert.False(await shell.RequestCloseAsync());
    }

    [Fact]
    public async Task ClosingWithUnsavedWorkCanDiscard()
    {
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Secondary));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        Assert.True(await shell.RequestCloseAsync());
    }

    [Fact]
    public async Task OpeningAVaultThatIsAlreadyOpenElsewhereIsRefused()
    {
        _singleInstance.TryAcquireVault(Arg.Any<string>()).Returns((IDisposable?)null);

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);

        _singleInstance.Received().FocusExistingInstance(_vaultPath);
        Assert.Equal(ShellMode.NoVault, shell.Mode);
    }

    [Fact]
    public async Task AMissingVaultIsForgottenRatherThanOpened()
    {
        File.Delete(_vaultPath);

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);

        _recent.Received().Forget(_vaultPath);
        Assert.Equal(ShellMode.NoVault, shell.Mode);
    }

    [Fact]
    public async Task AutoLockDropsTheExplorerAndTheCaptureExclusion()
    {
        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        _session.Lock();
        _autoLock.Locked += Raise.Event<EventHandler<AutoLockReason>>(_autoLock, AutoLockReason.Idle);

        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.Null(shell.Explorer);
        _privacy.Received().SetExcludeFromCapture(false);
    }

    [Fact]
    public async Task ZeroKeysLocksTheSession()
    {
        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        shell.ZeroKeys();

        Assert.True(_session.IsLocked);
    }

    [Fact]
    public async Task OpeningASecondVaultFromTheUnlockCardReleasesTheFirstVaultsLock()
    {
        // The unlock card keeps Session null, so the close path used to return before it got
        // anywhere near the single-instance lock: the first vault's named mutex was dropped on
        // the floor and never released, so that vault could not be opened again for the life of
        // the process.
        var first = new DisposeFlag();
        var second = new DisposeFlag();
        string other = _vaultPath + ".other";
        File.WriteAllBytes(other, VaultHeaderBytes());

        try
        {
            _singleInstance.TryAcquireVault(_vaultPath).Returns(_ => first);
            _singleInstance.TryAcquireVault(other).Returns(_ => second);

            ShellViewModel shell = NewShell();
            await shell.OpenVaultCommand.ExecuteAsync(null);
            Assert.Equal(ShellMode.Locked, shell.Mode);
            Assert.Null(shell.Session);
            Assert.False(first.Disposed);

            _files.PickVaultToOpen().Returns(other);
            await shell.OpenVaultCommand.ExecuteAsync(null);

            Assert.True(first.Disposed);
            Assert.False(second.Disposed);
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public async Task LockingResetsTheWindowTitle()
    {
        // The title is also the taskbar and Alt+Tab label, and lock clears state
        // (UI-CONTRACT.md section 1.10).
        _dialogs.ConfirmAsync(Arg.Any<ConfirmRequest>()).Returns(Task.FromResult(ConfirmResult.Secondary));

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);
        Assert.Contains(Path.GetFileNameWithoutExtension(_vaultPath), shell.Title, StringComparison.Ordinal);

        await shell.LockCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Locked, shell.Mode);
        Assert.Equal("Bastion", shell.Title);
    }

    [Fact]
    public async Task TheRollbackRecordIsKeyedOnTheVaultIdAndWarnsForAnOlderCopyAtAnotherPath()
    {
        // A path key misses the whole attack: an older copy handed back under another name is a
        // different path and hashes to nothing the machine has seen. The derived vault id
        // (FORMAT.md 2.4) travels with the bytes, so the copy is recognised wherever it turns up.
        var counters = new Dictionary<string, ulong>(StringComparer.Ordinal);
        _rollback.When(r => r.Record(Arg.Any<string>(), Arg.Any<ulong>()))
            .Do(call =>
            {
                string key = call.ArgAt<string>(0);
                ulong value = call.ArgAt<ulong>(1);
                if (!counters.TryGetValue(key, out ulong known) || value > known)
                {
                    counters[key] = value;
                }
            });
        _rollback.LastSeenCounter(Arg.Any<string>())
            .Returns(call => counters.TryGetValue(call.ArgAt<string>(0), out ulong seen) ? seen : null);

        ShellViewModel shell = NewShell();
        await shell.OpenVaultCommand.ExecuteAsync(null);
        await shell.Unlock.SubmitAsync(null, null);

        string identity = _session.VaultIdHex;
        Assert.Equal(32, identity.Length);
        Assert.Equal([identity], counters.Keys);
        Assert.Equal(_session.Statistics.SaveCounter, counters[identity]);
        Assert.Null(shell.Unlock.RollbackWarning);

        // Now the same vault, an older save, at a path this machine has never seen.
        string copy = _vaultPath + ".copy";
        File.WriteAllBytes(copy, VaultHeaderBytes());

        try
        {
            _session = new FakeVaultSession(copy, identity, _session.Statistics.SaveCounter - 3);
            _files.PickVaultToOpen().Returns(copy);

            await shell.OpenVaultCommand.ExecuteAsync(null);
            await shell.Unlock.SubmitAsync(null, null);

            Assert.NotNull(shell.Unlock.RollbackWarning);
            Assert.Contains("older copy", shell.Unlock.RollbackWarning, StringComparison.Ordinal);

            // The record is never walked backwards by looking at an old file.
            Assert.Equal(counters[identity], counters[_session.VaultIdHex]);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public async Task CreatingAVaultTakesTheSingleInstanceLockStraightAway()
    {
        // A vault that has just been created is as writable as any other, so it is protected from
        // the moment it exists rather than from the first time it is reopened.
        var held = new DisposeFlag();
        string fresh = _vaultPath + ".new";
        _singleInstance.TryAcquireVault(fresh).Returns(_ => held);
        _dialogs.ShowAsync(Arg.Any<NewVaultDialogViewModel>())
            .Returns(Task.FromResult<NewVaultResult?>(
                new NewVaultResult(fresh, null, null, KdfParameters.Default)));

        var created = new FakeVaultSession(fresh);
        _factory.CreateAsync(
                Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<KdfParameters>(),
                Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IVaultSession>(created));

        ShellViewModel shell = NewShell();
        await shell.NewVaultCommand.ExecuteAsync(null);

        Assert.Equal(ShellMode.Open, shell.Mode);
        Assert.Same(created, shell.Session);
        _singleInstance.Received().TryAcquireVault(fresh);
        Assert.False(held.Disposed);

        // And it is given back when the vault is closed.
        await shell.ShutdownAsync();
        Assert.True(held.Disposed);
    }

    [Fact]
    public async Task CreatingAVaultThatAnotherWindowHoldsIsRefusedBeforeAnythingIsWritten()
    {
        string fresh = _vaultPath + ".new";
        _singleInstance.TryAcquireVault(fresh).Returns((IDisposable?)null);
        _dialogs.ShowAsync(Arg.Any<NewVaultDialogViewModel>())
            .Returns(Task.FromResult<NewVaultResult?>(
                new NewVaultResult(fresh, null, null, KdfParameters.Default)));

        ShellViewModel shell = NewShell();
        await shell.NewVaultCommand.ExecuteAsync(null);

        _singleInstance.Received().FocusExistingInstance(fresh);
        await _factory.DidNotReceive().CreateAsync(
            Arg.Any<string>(), Arg.Any<Passphrase>(), Arg.Any<KeyFile?>(), Arg.Any<KdfParameters>(),
            Arg.Any<IProgress<VaultProgress>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(ShellMode.NoVault, shell.Mode);
        Assert.Null(shell.Session);
    }

    /// <summary>
    /// A 160-byte stand-in header: the magic Core writes, then a distinct salt at offset 32 so
    /// the App's header-derived rollback key has something to hash.
    /// </summary>
    private static byte[] VaultHeaderBytes()
    {
        byte[] header = new byte[160];
        ReadOnlySpan<byte> magic = [0x89, 0x42, 0x53, 0x54, 0x4E, 0x0D, 0x0A, 0x1A];
        magic.CopyTo(header);
        System.Security.Cryptography.RandomNumberGenerator.Fill(header.AsSpan(32, 32));
        return header;
    }

    private ShellViewModel NewShell()
    {
        var operation = new OperationViewModel(_dispatcher, _log);

        return new ShellViewModel(
            _factory,
            _dialogs,
            _files,
            _settings,
            _recent,
            _rollback,
            _clipboard,
            _autoLock,
            _privacy,
            _singleInstance,
            _shellIntegration,
            _estimator,
            _dispatcher,
            _log,
            operation,
            session => new ExplorerViewModel(
                session, _dialogs, _files, _clipboard, _osClipboard, _settings, _dispatcher, _log, operation));
    }
}
