using System.Globalization;
using Bastion.App.Services;
using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels;

/// <summary>The shell's state machine (UI-CONTRACT.md section 4).</summary>
public enum ShellMode
{
    /// <summary>No vault: the start screen with Create, Open and the recents.</summary>
    NoVault,

    /// <summary>A key derivation is running. Not cancellable.</summary>
    Unlocking,

    /// <summary>A vault is open and the explorer is live.</summary>
    Open,

    /// <summary>A vault is open but locked: the unlock card, same path, unsaved work intact.</summary>
    Locked,

    /// <summary>A modal operation owns the window (Save, Save copy, Change credentials).</summary>
    Busy,
}

/// <summary>What the 2 px state stripe under the title bar is showing.</summary>
public enum StripeState
{
    /// <summary>No vault: no stripe.</summary>
    None,

    /// <summary>Locked: a neutral stroke.</summary>
    Locked,

    /// <summary>Unlocked and saved: the dimmed lamp.</summary>
    Saved,

    /// <summary>Unsaved changes: amber, dashed.</summary>
    Unsaved,

    /// <summary>An operation is running: amber, shimmering.</summary>
    Running,

    /// <summary>An integrity failure was found: red.</summary>
    IntegrityFailure,
}

/// <summary>
/// The shell. It owns the session, the mode, the global commands and the one long-operation
/// runner; everything else in the window is a view over one of those. It is also the only place
/// that decides when keys get zeroed.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IVaultFactory _factory;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;
    private readonly ISettingsService _settings;
    private readonly IRecentVaults _recent;
    private readonly IRollbackGuard _rollback;
    private readonly IInternalClipboard _clipboard;
    private readonly IAutoLockController _autoLock;
    private readonly IScreenPrivacy _privacy;
    private readonly ISingleInstance _singleInstance;
    private readonly IShellIntegration _shellIntegration;
    private readonly IKdfEstimator _estimator;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILog _log;
    private readonly Func<IVaultSession, ExplorerViewModel> _explorerFactory;
    private readonly VaultChangeMarshaller _changes;

    private IDisposable? _vaultLock;
    private ProgressDialogViewModel? _progressDialog;

    /// <summary>Path of the keyfile the credentials currently in the header were made with, if any.</summary>
    private string? _headerKeyFilePath;

    /// <summary>Keyfile path of a credential change that is derived but not saved yet.</summary>
    private string? _pendingKeyFilePath;

    private bool _hasPendingKeyFileChange;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultOpen))]
    [NotifyPropertyChangedFor(nameof(IsStartVisible))]
    [NotifyPropertyChangedFor(nameof(IsUnlockVisible))]
    [NotifyPropertyChangedFor(nameof(IsExplorerVisible))]
    [NotifyPropertyChangedFor(nameof(IsShellStatusBarVisible))]
    [NotifyPropertyChangedFor(nameof(Stripe))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private ShellMode _mode = ShellMode.NoVault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    [NotifyPropertyChangedFor(nameof(VaultName))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private IVaultSession? _session;

    [ObservableProperty]
    private ExplorerViewModel? _explorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Stripe))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Stripe))]
    private bool _hasIntegrityFailure;

    [ObservableProperty]
    private PendingChangesViewModel? _pendingChanges;

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnStatusMessageChanged(string? value)
    {
        // While the explorer is up its status bar is the only one on screen, so it says what the shell
        // just did ("Saved - save #7", "Settings saved.") instead of losing the line.
        if (Explorer is { } explorer && !string.IsNullOrEmpty(value))
        {
            explorer.StatusBar.Message = value;
        }
    }

    [ObservableProperty]
    private VerifyReport? _lastVerifyReport;

    /// <summary>Creates the shell.</summary>
    /// <param name="factory">Core's vault factory.</param>
    /// <param name="dialogs">In-window dialogs.</param>
    /// <param name="files">OS file pickers.</param>
    /// <param name="settings">Application settings.</param>
    /// <param name="recent">Recent-vault list.</param>
    /// <param name="rollback">Per-machine save-counter record.</param>
    /// <param name="clipboard">Internal clipboard, cleared on lock.</param>
    /// <param name="autoLock">Auto-lock controller.</param>
    /// <param name="privacy">Screen-capture exclusion.</param>
    /// <param name="singleInstance">Per-vault process lock.</param>
    /// <param name="shellIntegration">Windows shell integration, offered by the Settings dialog.</param>
    /// <param name="estimator">KDF cost estimator for the credential dialogs.</param>
    /// <param name="dispatcher">UI thread marshaller.</param>
    /// <param name="log">Log.</param>
    /// <param name="operation">The shared long-operation runner.</param>
    /// <param name="explorerFactory">Creates the explorer for an open session.</param>
    public ShellViewModel(
        IVaultFactory factory,
        IDialogService dialogs,
        IFileDialogService files,
        ISettingsService settings,
        IRecentVaults recent,
        IRollbackGuard rollback,
        IInternalClipboard clipboard,
        IAutoLockController autoLock,
        IScreenPrivacy privacy,
        ISingleInstance singleInstance,
        IShellIntegration shellIntegration,
        IKdfEstimator estimator,
        IUiDispatcher dispatcher,
        ILog log,
        OperationViewModel operation,
        Func<IVaultSession, ExplorerViewModel> explorerFactory)
    {
        _factory = factory;
        _dialogs = dialogs;
        _files = files;
        _settings = settings;
        _recent = recent;
        _rollback = rollback;
        _clipboard = clipboard;
        _autoLock = autoLock;
        _privacy = privacy;
        _singleInstance = singleInstance;
        _shellIntegration = shellIntegration;
        _estimator = estimator;
        _dispatcher = dispatcher;
        _log = log;
        _explorerFactory = explorerFactory;

        Operation = operation;
        Unlock = new UnlockViewModel(files, log);
        Start = new StartViewModel(recent, NewVaultCommand, OpenVaultCommand, OpenRecentCommand);

        _changes = new VaultChangeMarshaller(dispatcher, OnVaultChanged);

        Operation.Started += OnOperationStarted;
        Operation.Finished += OnOperationFinished;
        _autoLock.Locked += OnAutoLocked;
    }

    /// <summary>Raised when the shell wants the window to close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the first-run screen should be shown.</summary>
    public event EventHandler? FirstRunRequested;

    /// <summary>The shared long-operation runner.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>The unlock card.</summary>
    public UnlockViewModel Unlock { get; }

    /// <summary>The no-vault screen.</summary>
    public StartViewModel Start { get; }

    /// <summary>True when a session exists, locked or not.</summary>
    public bool HasSession => Session is not null;

    /// <summary>True when the explorer should be live.</summary>
    public bool IsVaultOpen => Mode == ShellMode.Open || Mode == ShellMode.Busy;

    /// <summary>True when the start screen is the visible surface.</summary>
    public bool IsStartVisible => Mode == ShellMode.NoVault;

    /// <summary>True when the unlock card is the visible surface.</summary>
    public bool IsUnlockVisible => Mode is ShellMode.Locked or ShellMode.Unlocking;

    /// <summary>True when the explorer is the visible surface.</summary>
    public bool IsExplorerVisible => IsVaultOpen;

    /// <summary>
    /// True when the shell's own status strip is the one on screen. The explorer brings its own status
    /// bar, so the two would otherwise stack; shell-level messages are forwarded into it instead.
    /// </summary>
    public bool IsShellStatusBarVisible => !IsExplorerVisible;

    /// <summary>Name of the open vault, without extension.</summary>
    public string VaultName =>
        Session is null ? string.Empty : System.IO.Path.GetFileNameWithoutExtension(Session.Path);

    /// <summary>
    /// Window title: "Bastion", or "name - Bastion" with a bullet when there is unsaved work.
    /// A locked vault shows the bare product name: lock clears state (UI-CONTRACT.md section
    /// 1.10), and the window title is also the taskbar and Alt+Tab label, which is exactly what
    /// a locked screen must stop advertising.
    /// </summary>
    public string Title => Session is null || Mode is ShellMode.Locked or ShellMode.Unlocking
        ? "Bastion"
        : $"{VaultName}{(IsDirty ? " •" : string.Empty)} - Bastion";

    /// <summary>What the state stripe shows.</summary>
    public StripeState Stripe
    {
        get
        {
            if (HasIntegrityFailure)
            {
                return StripeState.IntegrityFailure;
            }

            if (Operation.IsRunning)
            {
                return StripeState.Running;
            }

            return Mode switch
            {
                ShellMode.NoVault => StripeState.None,
                ShellMode.Locked or ShellMode.Unlocking => StripeState.Locked,
                _ => IsDirty ? StripeState.Unsaved : StripeState.Saved,
            };
        }
    }

    /// <summary>Zeroes every key the process holds. Called first from every crash handler.</summary>
    public void ZeroKeys()
    {
        try
        {
            Session?.ZeroKeys();
        }
        catch (Exception ex)
        {
            // A crash handler must not throw a second time.
            _log.Error("ZeroKeys failed.", ex);
        }
    }

    /// <summary>Opens a vault named on the command line, or shows the first-run screen.</summary>
    /// <param name="vaultPath">Path from the command line, or <see langword="null"/>.</param>
    public async Task StartupAsync(string? vaultPath)
    {
        if (_settings.Current.ShowFirstRun)
        {
            FirstRunRequested?.Invoke(this, EventArgs.Empty);
        }

        if (!string.IsNullOrWhiteSpace(vaultPath))
        {
            await BeginOpenAsync(vaultPath).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Decides whether the window may close, prompting when there is unsaved work.
    /// </summary>
    /// <returns>True when the window may close.</returns>
    public async Task<bool> RequestCloseAsync()
    {
        if (Operation.IsRunning)
        {
            await _dialogs.ShowInfoAsync(
                "An operation is running",
                "Wait for it to finish, or cancel it, before closing Bastion.").ConfigureAwait(true);
            return false;
        }

        if (Session is not { IsDirty: true } session)
        {
            return true;
        }

        PendingChanges pending = session.Pending;
        int count = pending.Added + pending.Changed + pending.Deleted;
        ConfirmResult answer = await _dialogs.ConfirmAsync(new ConfirmRequest(
            $"Close with {count} unsaved change{(count == 1 ? string.Empty : "s")}?",
            "Unsaved changes live only in this session. Closing without saving throws them away.",
            PrimaryVerb: "Save and close",
            CancelVerb: "Cancel",
            SecondaryVerb: $"Discard {count} change{(count == 1 ? string.Empty : "s")}",
            IsDestructive: false)).ConfigureAwait(true);

        switch (answer)
        {
            case ConfirmResult.Primary:
                await SaveAsync().ConfigureAwait(true);
                return !session.IsDirty;

            case ConfirmResult.Secondary:
                return true;

            default:
                return false;
        }
    }

    /// <summary>Closes the session and releases everything it held.</summary>
    public async Task ShutdownAsync()
    {
        _autoLock.Session = null;
        _changes.Detach();

        if (Session is { } session)
        {
            session.ZeroKeys();
            await session.DisposeAsync().ConfigureAwait(true);
        }

        Session = null;
        ReleaseVaultLock();
    }

    /// <summary>Releases the single-instance lock, if one is held. Safe to call repeatedly.</summary>
    private void ReleaseVaultLock()
    {
        _vaultLock?.Dispose();
        _vaultLock = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Operation.Started -= OnOperationStarted;
        Operation.Finished -= OnOperationFinished;
        _autoLock.Locked -= OnAutoLocked;
        _changes.Dispose();
        Explorer?.Dispose();
        ReleaseVaultLock();
    }

    // ───────────────────────────── commands ─────────────────────────────

    /// <summary>Creates a new vault and opens it.</summary>
    [RelayCommand]
    public async Task NewVaultAsync()
    {
        var dialog = new NewVaultDialogViewModel(_files, _estimator, _settings.Current.DefaultKdfPreset, _log);

        using var measurement = new CancellationTokenSource();
        Task measuring = dialog.MeasurePresetsAsync(measurement.Token);

        NewVaultResult? result = await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
        await StopMeasuringAsync(measurement, measuring).ConfigureAwait(true);

        if (result is null)
        {
            return;
        }

        using Passphrase? password = result.Password;
        using KeyFile? keyFile = result.KeyFile;

        if (!await CloseCurrentAsync().ConfigureAwait(true))
        {
            return;
        }

        // A vault is protected by the per-vault single-instance lock from the moment it exists, not
        // from the first time it is reopened: the create path takes it exactly as the open path does,
        // and a path another window already holds is refused before a single byte is written.
        ReleaseVaultLock();
        _vaultLock = _singleInstance.TryAcquireVault(result.Path);
        if (_vaultLock is null)
        {
            _singleInstance.FocusExistingInstance(result.Path);
            await _dialogs.ShowInfoAsync(
                "That vault is already open",
                "Bastion opened it in another window. Two processes writing one vault produce a conflict, not a merge.").ConfigureAwait(true);
            return;
        }

        try
        {
            Mode = ShellMode.Unlocking;
            IVaultSession? created = await Operation.RunAsync(
                VaultOperation.Create,
                "Creating vault",
                (progress, ct) => _factory.CreateAsync(result.Path, password!, keyFile, result.Kdf, progress, ct))
                .ConfigureAwait(true);

            if (created is null)
            {
                ReleaseVaultLock();
                Mode = ShellMode.NoVault;
                return;
            }

            AdoptSession(created);
            _headerKeyFilePath = keyFile?.SourcePath;
            _recent.Touch(result.Path);
            StatusMessage = "Vault created.";
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
        {
            ReleaseVaultLock();
            Mode = ShellMode.NoVault;
            await ReportAsync("The vault could not be created", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Opens a vault through the file picker.</summary>
    [RelayCommand]
    public async Task OpenVaultAsync()
    {
        string? path = _files.PickVaultToOpen();
        if (path is not null)
        {
            await BeginOpenAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Opens one entry of the recents list.</summary>
    /// <param name="path">Full path of the vault.</param>
    [RelayCommand]
    public async Task OpenRecentAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            await BeginOpenAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>Commits every pending change.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var options = new SaveOptions(_settings.Current.SizeObfuscation);
        Mode = ShellMode.Busy;

        try
        {
            bool completed = await Operation.RunAsync(
                VaultOperation.Save,
                "Saving vault",
                (progress, ct) => session.SaveAsync(options, progress, ct)).ConfigureAwait(true);

            if (completed)
            {
                if (_hasPendingKeyFileChange)
                {
                    _headerKeyFilePath = _pendingKeyFilePath;
                    _pendingKeyFilePath = null;
                    _hasPendingKeyFileChange = false;
                }

                RecordCounter(session);
                StatusMessage = $"Saved · save #{session.Statistics.SaveCounter}";
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
        {
            await ReportAsync("The vault could not be saved", ex).ConfigureAwait(true);
        }
        finally
        {
            Mode = session.IsLocked ? ShellMode.Locked : ShellMode.Open;
            RefreshVaultState();
        }
    }

    /// <summary>Writes a re-keyed copy of the current state to a new file.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveCopyAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        var dialog = new NewVaultDialogViewModel(_files, _estimator, _settings.Current.DefaultKdfPreset, _log)
        {
            Title = "Save a copy",
        };
        using var measurement = new CancellationTokenSource();
        Task measuring = dialog.MeasurePresetsAsync(measurement.Token);

        NewVaultResult? result = await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
        await StopMeasuringAsync(measurement, measuring).ConfigureAwait(true);

        if (result is null)
        {
            return;
        }

        using Passphrase? password = result.Password;
        using KeyFile? keyFile = result.KeyFile;

        Mode = ShellMode.Busy;
        try
        {
            var options = new SaveOptions(_settings.Current.SizeObfuscation);
            bool completed = await Operation.RunAsync(
                VaultOperation.SaveCopy,
                "Writing a copy",
                (progress, ct) => session.SaveCopyAsync(result.Path, password!, keyFile, result.Kdf, options, progress, ct))
                .ConfigureAwait(true);

            if (completed)
            {
                StatusMessage = "Copy written.";
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
        {
            await ReportAsync("The copy could not be written", ex).ConfigureAwait(true);
        }
        finally
        {
            Mode = ShellMode.Open;
        }
    }

    /// <summary>Locks the vault, prompting when there is unsaved work.</summary>
    [RelayCommand(CanExecute = nameof(CanLock))]
    public async Task LockAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        if (session.IsDirty)
        {
            PendingChanges pending = session.Pending;
            int count = pending.Added + pending.Changed + pending.Deleted;
            ConfirmResult answer = await _dialogs.ConfirmAsync(new ConfirmRequest(
                $"Lock with {count} unsaved change{(count == 1 ? string.Empty : "s")}?",
                "Locking zeroes the keys. Unsaved changes stay in this session and come back when you unlock.",
                PrimaryVerb: "Save and lock",
                CancelVerb: "Cancel",
                SecondaryVerb: "Lock without saving")).ConfigureAwait(true);

            if (answer == ConfirmResult.Cancel)
            {
                return;
            }

            if (answer == ConfirmResult.Primary)
            {
                await SaveAsync().ConfigureAwait(true);
                if (session.IsDirty)
                {
                    return;
                }
            }
        }

        LockNow();
    }

    /// <summary>Changes the password, keyfile or KDF parameters; applied at the next save.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task ChangeCredentialsAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        // The dialog is single-use, so a wrong current password re-opens a fresh one carrying the
        // error and everything the user had already chosen.
        string? error = null;
        KdfParameters preset = session.Kdf;
        CredentialChangeMode mode = CredentialChangeMode.Rekey;
        string? keyFilePath = null;

        while (true)
        {
            var dialog = new ChangeCredentialsDialogViewModel(
                _files, _estimator, preset, session.Statistics.TotalPlaintextBytes, _log)
            {
                Error = error,
                Mode = mode,
                KeyFilePath = keyFilePath,
            };

            using var measurement = new CancellationTokenSource();
            Task measuring = dialog.MeasurePresetsAsync(measurement.Token);
            ChangeCredentialsResult? result = await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
            await StopMeasuringAsync(measurement, measuring).ConfigureAwait(true);

            if (result is null)
            {
                return;
            }

            preset = result.Kdf;
            mode = result.Mode;
            keyFilePath = dialog.KeyFilePath;

            using Passphrase? current = result.CurrentPassword;
            using Passphrase? newPassword = result.NewPassword;
            using KeyFile? keyFile = result.KeyFile;

            CurrentPasswordCheck check = await CheckCurrentPasswordAsync(session, current).ConfigureAwait(true);
            if (check == CurrentPasswordCheck.Cancelled)
            {
                return;
            }

            if (check == CurrentPasswordCheck.Wrong)
            {
                error = _headerKeyFilePath is null
                    ? "That is not the current password."
                    : "That password does not open this vault with the keyfile it was unlocked with.";
                continue;
            }

            Mode = ShellMode.Busy;
            try
            {
                bool completed = await Operation.RunAsync(
                    VaultOperation.ChangeCredentials,
                    "Changing credentials",
                    (progress, ct) => session.ChangeCredentialsAsync(newPassword!, keyFile, result.Kdf, result.Mode, progress, ct))
                    .ConfigureAwait(true);

                if (completed)
                {
                    // The header still holds the old credentials until the save commits them.
                    _pendingKeyFilePath = keyFile?.SourcePath;
                    _hasPendingKeyFileChange = true;
                    StatusMessage = "New credentials are pending; they are applied at the next save.";
                }
            }
            catch (Exception ex) when (ex is VaultException or NotImplementedException)
            {
                await ReportAsync("The credentials could not be changed", ex).ConfigureAwait(true);
            }
            finally
            {
                Mode = ShellMode.Open;
                RefreshVaultState();
            }

            return;
        }
    }

    /// <summary>Outcome of checking the current password before new credentials are accepted.</summary>
    private enum CurrentPasswordCheck
    {
        /// <summary>The password (and keyfile) open this vault.</summary>
        Ok,

        /// <summary>They do not.</summary>
        Wrong,

        /// <summary>The user cancelled the key derivation.</summary>
        Cancelled,
    }

    /// <summary>
    /// Re-derives the KEK from the header and confirms the typed password really is the current one.
    /// The keyfile is the one the session was opened with; Core answers without touching any state.
    /// </summary>
    /// <param name="session">The open session.</param>
    /// <param name="current">The password the user typed, or <see langword="null"/> in the demo host.</param>
    private async Task<CurrentPasswordCheck> CheckCurrentPasswordAsync(IVaultSession session, Passphrase? current)
    {
        if (current is null)
        {
            // The --demo host holds no key material; there is nothing to check against.
            return CurrentPasswordCheck.Ok;
        }

        KeyFile? keyFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_headerKeyFilePath) && File.Exists(_headerKeyFilePath))
            {
                keyFile = KeyFile.Load(_headerKeyFilePath);
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("The keyfile this vault was opened with could not be re-read for the password check.", ex);
        }

        Mode = ShellMode.Busy;
        try
        {
            bool? verified = await Operation.RunAsync(
                VaultOperation.ChangeCredentials,
                "Checking the current password",
                (_, ct) => session.VerifyPasswordAsync(current, keyFile, ct))
                .ConfigureAwait(true);

            return verified switch
            {
                true => CurrentPasswordCheck.Ok,
                false => CurrentPasswordCheck.Wrong,
                null => CurrentPasswordCheck.Cancelled,
            };
        }
        catch (NotImplementedException ex)
        {
            // An older Core cannot answer; the field stays a speed bump rather than blocking the user.
            _log.Warn("This build of Bastion.Core cannot verify a password; the current-password field was not checked.", ex);
            return CurrentPasswordCheck.Ok;
        }
        catch (Exception ex) when (ex is VaultException or IOException)
        {
            await ReportAsync("The current password could not be checked", ex).ConfigureAwait(true);
            return CurrentPasswordCheck.Cancelled;
        }
        finally
        {
            keyFile?.Dispose();
            Mode = ShellMode.Open;
        }
    }

    /// <summary>Authenticates every blob and shows the report.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task VerifyAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            VerifyReport? report = await Operation.RunAsync(
                VaultOperation.Verify,
                "Verifying vault",
                (progress, ct) => session.VerifyAsync(progress, ct),
                isModal: false).ConfigureAwait(true);

            if (report is null)
            {
                StatusMessage = "Verify cancelled.";
                return;
            }

            LastVerifyReport = report;
            HasIntegrityFailure = !report.IsClean;
            await _dialogs.ShowAsync(new VerifyReportDialogViewModel(report, VaultName)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is VaultException or IOException or NotImplementedException)
        {
            await ReportAsync("The vault could not be verified", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Best-effort export of everything that still authenticates.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task RecoverAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        string? destination = _files.PickExportFolder();
        if (destination is null)
        {
            return;
        }

        try
        {
            var options = new ExportOptions(WritePartialFiles: true);
            ExportResult? result = await Operation.RunAsync(
                VaultOperation.Recover,
                "Recovering vault",
                (progress, ct) => session.RecoverAsync(destination, options, progress, ct),
                isModal: false).ConfigureAwait(true);

            if (result is not null)
            {
                StatusMessage =
                    $"Recovered {result.FilesWritten:N0} file{Plural(result.FilesWritten)} · {OperationViewModel.FormatBytes(result.BytesWritten)}";
            }
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
        {
            await ReportAsync("The vault could not be recovered", ex).ConfigureAwait(true);
        }
    }

    /// <summary>Shows the Settings dialog and applies what it returns.</summary>
    [RelayCommand]
    public async Task ShowSettingsAsync()
    {
        var dialog = new SettingsDialogViewModel(_settings, _shellIntegration, _recent, _files, _log);
        AppSettings? edited = await _dialogs.ShowAsync(dialog).ConfigureAwait(true);
        if (edited is null)
        {
            return;
        }

        _settings.Current.CopyFrom(edited);
        _settings.Save();
        _autoLock.ApplySettings();
        _privacy.SetExcludeFromCapture(_settings.Current.ExcludeFromScreenCapture && Mode != ShellMode.Locked);
        StatusMessage = "Settings saved.";
    }

    /// <summary>Shows the keyboard shortcuts.</summary>
    [RelayCommand]
    public Task ShowShortcutsAsync() => _dialogs.ShowAsync(new ShortcutsDialogViewModel());

    /// <summary>Shows the About dialog.</summary>
    [RelayCommand]
    public Task ShowAboutAsync()
    {
        string version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return _dialogs.ShowAsync(new AboutDialogViewModel(version));
    }

    /// <summary>Shows the properties of the open vault.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public Task ShowVaultPropertiesAsync()
    {
        if (Session is not { } session)
        {
            return Task.CompletedTask;
        }

        var dialog = new PropertiesDialogViewModel(
            session.Path,
            session.Statistics,
            session.Kdf,
            session.Pending,
            _settings.Current.SizeObfuscation,
            _settings.Current.ReencryptOnSave);

        return _dialogs.ShowAsync(dialog);
    }

    /// <summary>Asks the window to close.</summary>
    [RelayCommand]
    public void Exit() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ───────────────────────────── internals ─────────────────────────────

    /// <summary>
    /// Stops the KDF benchmarks a dialog started and waits for them to unwind. Each preset runs a
    /// real Argon2id pass - Strong is 512 MiB or more - so a dialog dismissed with Escape a moment
    /// after it opened used to keep its command "running" for several seconds, and
    /// <c>AsyncRelayCommand</c> reports CanExecute false for that whole time: Ctrl+N, Save a copy
    /// and Change credentials went dead with nothing on screen to explain it.
    /// </summary>
    /// <param name="measurement">Token source driving the benchmarks.</param>
    /// <param name="measuring">The benchmark task.</param>
    private async Task StopMeasuringAsync(CancellationTokenSource measurement, Task measuring)
    {
        try
        {
            await measurement.CancelAsync().ConfigureAwait(true);
            await measuring.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected: the dialog closed before every preset had been measured.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tearing down an estimate must not be able to take the command down with it, and it
            // must not be able to end the process without saying why either.
            _log.Warn("The key-derivation benchmark did not stop cleanly.", ex);
        }
    }

    /// <summary>
    /// Highest counter recorded for this vault, or null when the machine has not seen it before.
    /// <para>
    /// The record is keyed on the derived vault id (FORMAT.md section 2.4), never on where the file
    /// happens to sit. A path key misses the whole attack it exists to catch: hand the victim an
    /// older copy under any other name - "vault (1).bastion", a USB stick, a restore into another
    /// folder - and a path-keyed record simply does not match, so no warning is shown. The vault id
    /// travels with the bytes, and it rotates exactly when the vault key does, which is what makes
    /// it a key space identity rather than a file identity.
    /// </para>
    /// </summary>
    /// <param name="session">The open session.</param>
    private ulong? LastSeenCounter(IVaultSession session) => _rollback.LastSeenCounter(session.VaultIdHex);

    /// <summary>Records the counter just observed under this vault's identity.</summary>
    /// <param name="session">The open session.</param>
    private void RecordCounter(IVaultSession session)
    {
        // A save may have rotated the identity (a re-key does), so the id is re-read here and the
        // new key space starts at the counter just written.
        _rollback.Record(session.VaultIdHex, session.Statistics.SaveCounter);
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private bool CanSave() => Session is not null && Mode == ShellMode.Open && !Operation.IsRunning;

    private bool CanLock() => Session is not null && Mode == ShellMode.Open && !Operation.IsRunning;

    private async Task BeginOpenAsync(string path)
    {
        if (!await CloseCurrentAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!File.Exists(path))
        {
            await _dialogs.ShowErrorAsync(
                "That vault is not there any more",
                "The file could not be found. It may have been moved, renamed or deleted.",
                path).ConfigureAwait(true);
            _recent.Forget(path);
            return;
        }

        ReleaseVaultLock();
        _vaultLock = _singleInstance.TryAcquireVault(path);
        if (_vaultLock is null)
        {
            _singleInstance.FocusExistingInstance(path);
            await _dialogs.ShowInfoAsync(
                "That vault is already open",
                "Bastion opened it in another window. Two processes writing one vault produce a conflict, not a merge.").ConfigureAwait(true);
            return;
        }

        KdfParameters? kdf = null;
        try
        {
            VaultHeaderInfo header = await _factory.ReadHeaderAsync(path, CancellationToken.None).ConfigureAwait(true);
            kdf = header.Kdf;
        }
        catch (VaultFormatException ex)
        {
            ReleaseVaultLock();
            await _dialogs.ShowErrorAsync(
                "This is not a Bastion vault",
                "The header does not describe a vault this version can read.",
                ex.Code.ToString()).ConfigureAwait(true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReleaseVaultLock();
            await ReportAsync("The vault could not be read", ex).ConfigureAwait(true);
            return;
        }
        catch (NotImplementedException ex)
        {
            // Core's header reader is not built yet; the unlock card simply says less.
            _log.Warn("The header could not be read; the unlock card falls back to a generic line.", ex);
        }

        string? rememberedKeyFile = _settings.Current.RememberKeyFilePaths
            ? _recent.Items.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))?.KeyFilePath
            : null;

        Unlock.Configure(path, kdf, rememberedKeyFile);
        Unlock.UnlockRequested = (password, keyFile, ct) => OpenFromDiskAsync(path, password, keyFile, ct);
        Mode = ShellMode.Locked;
    }

    private async Task<UnlockOutcome> OpenFromDiskAsync(
        string path, Passphrase? password, KeyFile? keyFile, CancellationToken ct)
    {
        Mode = ShellMode.Unlocking;

        try
        {
            var options = new OpenOptions(
                ReadOnly: false,
                StagingDirectoryOverride: StagingOverride());

            IVaultSession? opened = await Operation.RunAsync(
                VaultOperation.Open,
                "Opening vault",
                (progress, token) => _factory.OpenAsync(path, password!, keyFile, options, progress, token),
                isModal: true,
                ct).ConfigureAwait(true);

            if (opened is null)
            {
                Mode = ShellMode.Locked;
                return UnlockOutcome.Cancelled;
            }

            AdoptSession(opened);
            _headerKeyFilePath = keyFile?.SourcePath ?? Unlock.KeyFilePath;
            _recent.Touch(path);
            if (_settings.Current.RememberKeyFilePaths)
            {
                _recent.RememberKeyFile(path, Unlock.KeyFilePath);
            }

            ulong? lastSeen = LastSeenCounter(opened);
            Unlock.ReportOpened(opened.Statistics, lastSeen);
            RecordCounter(opened);
            StatusMessage = Unlock.StatusLine;

            return UnlockOutcome.Success;
        }
        catch (VaultAuthenticationException)
        {
            Mode = ShellMode.Locked;
            return UnlockOutcome.WrongCredentials;
        }
        catch (VaultFormatException ex)
        {
            Mode = ShellMode.Locked;
            return ex.Code is VaultErrorCode.IndexCorrupt or VaultErrorCode.IndexInvalid
                ? UnlockOutcome.Damaged
                : UnlockOutcome.NotAVault;
        }
        catch (VaultIntegrityException)
        {
            Mode = ShellMode.Locked;
            return UnlockOutcome.Damaged;
        }
        catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
        {
            _log.Error("The vault could not be opened.", ex);
            Mode = ShellMode.Locked;
            return UnlockOutcome.Unreadable;
        }
    }

    private async Task<UnlockOutcome> ReopenAsync(Passphrase? password, KeyFile? keyFile, CancellationToken ct)
    {
        if (Session is not { } session)
        {
            return UnlockOutcome.Unreadable;
        }

        Mode = ShellMode.Unlocking;

        try
        {
            bool completed = await Operation.RunAsync(
                VaultOperation.Open,
                "Unlocking vault",
                (progress, token) => session.UnlockAsync(password!, keyFile, progress, token),
                isModal: true,
                ct).ConfigureAwait(true);

            if (!completed)
            {
                Mode = ShellMode.Locked;
                return UnlockOutcome.Cancelled;
            }

            Mode = ShellMode.Open;
            _privacy.SetExcludeFromCapture(_settings.Current.ExcludeFromScreenCapture);
            Explorer ??= _explorerFactory(session);
            Explorer.Refresh();
            RefreshVaultState();
            return UnlockOutcome.Success;
        }
        catch (VaultAuthenticationException)
        {
            Mode = ShellMode.Locked;
            return UnlockOutcome.WrongCredentials;
        }
        catch (Exception ex) when (ex is VaultException or IOException or NotImplementedException)
        {
            _log.Error("The vault could not be unlocked.", ex);
            Mode = ShellMode.Locked;
            return UnlockOutcome.Unreadable;
        }
    }

    private string? StagingOverride() => _settings.Current.StagingLocation switch
    {
        StagingLocation.SystemTemp => System.IO.Path.GetTempPath(),
        StagingLocation.Custom when !string.IsNullOrWhiteSpace(_settings.Current.StagingCustomPath)
            => _settings.Current.StagingCustomPath,
        _ => null,
    };

    private void AdoptSession(IVaultSession session)
    {
        Session = session;
        _changes.Attach(session);
        _autoLock.Session = session;

        Explorer?.Dispose();
        Explorer = _explorerFactory(session);
        PendingChanges = new PendingChangesViewModel(session, UndoCommand);

        Mode = ShellMode.Open;
        HasIntegrityFailure = false;
        _privacy.SetExcludeFromCapture(_settings.Current.ExcludeFromScreenCapture);
        RefreshVaultState();

        // Put the keyboard in the explorer. Hiding the unlock card drops focus on the window, and
        // a window with focus routes none of the Explorer-scope keymap, so arrow keys, typeahead,
        // F2, Delete and Ctrl+Shift+N all do nothing until the user clicks a row. Two hops: the
        // first lets the dialog host finish unwinding, the second lets the list populate.
        ExplorerViewModel opened = Explorer;
        _dispatcher.Post(() => _dispatcher.Post(() =>
        {
            if (!_disposed && ReferenceEquals(Explorer, opened) && Mode == ShellMode.Open)
            {
                opened.FocusList();
            }
        }));
    }

    private async Task<bool> CloseCurrentAsync()
    {
        if (Session is null)
        {
            // The unlock card holds a single-instance lock for a vault that was never opened,
            // and Session stays null for the whole of that state. Dropping the field without
            // disposing would strand its named mutex for the life of the process, so that vault
            // could never be opened again by this or any other instance.
            ReleaseVaultLock();
            return true;
        }

        if (!await RequestCloseAsync().ConfigureAwait(true))
        {
            return false;
        }

        await ShutdownAsync().ConfigureAwait(true);
        Explorer?.Dispose();
        Explorer = null;
        PendingChanges = null;
        _headerKeyFilePath = null;
        _pendingKeyFilePath = null;
        _hasPendingKeyFileChange = false;
        _clipboard.Clear();
        Mode = ShellMode.NoVault;
        return true;
    }

    private void LockNow()
    {
        if (Session is not { } session)
        {
            return;
        }

        session.Lock();

        // Lock clears state (UI-CONTRACT.md section 1.10): the explorer leaves the visual tree,
        // the internal clipboard is emptied, and capture exclusion is dropped because a lock
        // screen is safe to photograph.
        Explorer?.Dispose();
        Explorer = null;
        _clipboard.Clear();
        _privacy.SetExcludeFromCapture(false);

        Unlock.Configure(session.Path, session.Kdf, Unlock.KeyFilePath);
        Unlock.UnlockRequested = ReopenAsync;
        Mode = ShellMode.Locked;
        StatusMessage = "Locked.";
        RefreshVaultState();
    }

    /// <summary>Undo, exposed here because the pending-changes popover offers it too.</summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (Session is not { CanUndo: true } session)
        {
            return;
        }

        try
        {
            await session.UndoAsync(CancellationToken.None).ConfigureAwait(true);
            RefreshVaultState();
        }
        catch (Exception ex) when (ex is VaultException or NotImplementedException)
        {
            await ReportAsync("That change could not be undone", ex).ConfigureAwait(true);
        }
    }

    private void OnAutoLocked(object? sender, AutoLockReason reason)
    {
        if (Session is null || Mode == ShellMode.Locked)
        {
            return;
        }

        Explorer?.Dispose();
        Explorer = null;
        _clipboard.Clear();
        _privacy.SetExcludeFromCapture(false);

        Unlock.Configure(Session.Path, Session.Kdf, Unlock.KeyFilePath);
        Unlock.UnlockRequested = ReopenAsync;
        Mode = ShellMode.Locked;
        StatusMessage = reason switch
        {
            AutoLockReason.Idle => "Locked after being idle.",
            AutoLockReason.SessionLocked => "Locked with the workstation.",
            AutoLockReason.Suspending => "Locked before sleep.",
            _ => "Locked.",
        };
    }

    private void OnVaultChanged(VaultChangedEventArgs e)
    {
        RefreshVaultState();
        Explorer?.Refresh();
        PendingChanges?.Refresh();
    }

    private void RefreshVaultState()
    {
        IsDirty = Session?.IsDirty ?? false;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(VaultName));
        OnPropertyChanged(nameof(Stripe));
        SaveCommand.NotifyCanExecuteChanged();
        SaveCopyCommand.NotifyCanExecuteChanged();
        LockCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        RecoverCommand.NotifyCanExecuteChanged();
        ChangeCredentialsCommand.NotifyCanExecuteChanged();
        ShowVaultPropertiesCommand.NotifyCanExecuteChanged();
    }

    private void OnOperationStarted(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Stripe));
        RefreshVaultState();

        if (!Operation.IsModal)
        {
            return;
        }

        _progressDialog = new ProgressDialogViewModel(Operation);
        _ = _dialogs.ShowAsync(_progressDialog);
    }

    private void OnOperationFinished(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Stripe));
        RefreshVaultState();

        ProgressDialogViewModel? dialog = _progressDialog;
        _progressDialog = null;
        dialog?.Complete(!Operation.WasCancelled);

        // An auto-lock that arrived while this operation was running was held back rather than
        // zeroing the keys underneath it; now is when it gets to happen.
        _autoLock.ResumeDeferred();
    }

    private async Task ReportAsync(string title, Exception ex)
    {
        _log.Error(title, ex);

        string message = ex switch
        {
            VaultResourceException resource =>
                $"Not enough room: {OperationViewModel.FormatBytes(resource.RequiredBytes)} needed, {OperationViewModel.FormatBytes(resource.AvailableBytes)} available.",
            VaultIoException { Code: VaultErrorCode.Locked } =>
                "The file is locked by another program. Close whatever is holding it and try again.",
            VaultIoException { Code: VaultErrorCode.ChangedOnDisk } =>
                "The file changed on disk since it was opened. Saving now would overwrite someone else's work.",
            VaultIntegrityException =>
                "A part of the vault failed authentication. Do not save over the file; use Recover to get out what still verifies.",
            VaultException vault => $"Core reported {vault.Code}.",
            NotImplementedException => "This part of Bastion.Core is not implemented in this build.",
            _ => ex.Message,
        };

        await _dialogs.ShowErrorAsync(title, message, ex.GetType().Name).ConfigureAwait(true);
    }
}
