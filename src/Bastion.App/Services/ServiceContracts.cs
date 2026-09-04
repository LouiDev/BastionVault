using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;

namespace Bastion.App.Services;

/// <summary>Which button of a confirmation the user pressed.</summary>
public enum ConfirmResult
{
    /// <summary>The dialog was dismissed (Escape, close, or the cancel button).</summary>
    Cancel,

    /// <summary>The affirmative verb was pressed.</summary>
    Primary,

    /// <summary>The second verb was pressed, where one exists ("Discard", "Lock without saving").</summary>
    Secondary,
}

/// <summary>
/// A confirmation to put in front of the user. The title is a verb plus a count, the buttons are
/// verbs, and a destructive verb is never the default (UI-CONTRACT.md section 7).
/// </summary>
/// <param name="Title">Verb and count, for example "Delete 12 items".</param>
/// <param name="Body">One or two sentences saying what will happen.</param>
/// <param name="PrimaryVerb">Label of the affirmative button.</param>
/// <param name="CancelVerb">Label of the dismissing button.</param>
/// <param name="SecondaryVerb">Label of the optional third button, or <see langword="null"/>.</param>
/// <param name="IsDestructive">True when the primary verb destroys something: it is styled danger and is not the default.</param>
/// <param name="Detail">Optional monospaced detail line (a path, a count, a KDF summary).</param>
public sealed record ConfirmRequest(
    string Title,
    string Body,
    string PrimaryVerb = "OK",
    string CancelVerb = "Cancel",
    string? SecondaryVerb = null,
    bool IsDestructive = false,
    string? Detail = null);

/// <summary>Shows modal dialogs inside the shell window (UI-CONTRACT.md section 1.8).</summary>
public interface IDialogService
{
    /// <summary>Shows a dialog and completes with its result, or <see langword="default"/> when it was cancelled.</summary>
    /// <typeparam name="TResult">Type the dialog produces.</typeparam>
    /// <param name="dialog">The dialog view model; its view is resolved by data template.</param>
    /// <param name="ct">Cancels the dialog from the caller's side.</param>
    Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> dialog, CancellationToken ct = default);

    /// <summary>Shows a confirmation whose buttons are verbs.</summary>
    /// <param name="request">What to ask.</param>
    Task<ConfirmResult> ConfirmAsync(ConfirmRequest request);

    /// <summary>Reports a failure with an optional expandable detail block.</summary>
    /// <param name="title">Short summary, for example "Could not save the vault".</param>
    /// <param name="message">What happened and what the user can do.</param>
    /// <param name="details">Technical detail, shown behind "Details"; never contains vault content.</param>
    Task ShowErrorAsync(string title, string message, string? details = null);

    /// <summary>Reports a completed action that needs acknowledgement but has no choice attached.</summary>
    /// <param name="title">Short summary.</param>
    /// <param name="message">The message body.</param>
    Task ShowInfoAsync(string title, string message);
}

/// <summary>Wraps the OS file pickers; every picker is shown with the shell window as its owner.</summary>
public interface IFileDialogService
{
    /// <summary>Picks an existing vault file to open, or returns <see langword="null"/>.</summary>
    string? PickVaultToOpen();

    /// <summary>Picks the path of a vault to create, or returns <see langword="null"/>.</summary>
    /// <param name="suggestedName">File name proposed in the dialog, without extension.</param>
    string? PickVaultToCreate(string suggestedName);

    /// <summary>Picks an existing keyfile, or returns <see langword="null"/>.</summary>
    string? PickKeyFile();

    /// <summary>Picks the path of a keyfile to generate, or returns <see langword="null"/>.</summary>
    string? PickKeyFileToCreate();

    /// <summary>Picks files to import; empty when the user cancelled.</summary>
    IReadOnlyList<string> PickFilesToImport();

    /// <summary>Picks a folder to import, or returns <see langword="null"/>.</summary>
    string? PickFolderToImport();

    /// <summary>Picks the destination directory of an export, or returns <see langword="null"/>.</summary>
    string? PickExportFolder();
}

/// <summary>Reads and writes <see cref="AppSettings"/>.</summary>
public interface ISettingsService
{
    /// <summary>The live settings instance; mutate it and call <see cref="Save"/>.</summary>
    AppSettings Current { get; }

    /// <summary>Writes the settings atomically (temp file plus replace).</summary>
    void Save();

    /// <summary>Raised after <see cref="Save"/> so views can re-read a value.</summary>
    event EventHandler? Changed;
}

/// <summary>The recent-vault list, stored DPAPI-protected for the current user.</summary>
public interface IRecentVaults
{
    /// <summary>Most recently opened first.</summary>
    IReadOnlyList<RecentVault> Items { get; }

    /// <summary>Records that a vault was opened now, moving it to the front.</summary>
    /// <param name="path">Full path of the vault.</param>
    void Touch(string path);

    /// <summary>Records the keyfile used with a vault; only called when the user opted in.</summary>
    /// <param name="path">Full path of the vault.</param>
    /// <param name="keyFilePath">Full path of the keyfile, or <see langword="null"/> to forget it.</param>
    void RememberKeyFile(string path, string? keyFilePath);

    /// <summary>Drops one vault from the list.</summary>
    /// <param name="path">Full path of the vault.</param>
    void Forget(string path);

    /// <summary>Drops the whole list.</summary>
    void Clear();

    /// <summary>Raised after any change.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Per-machine record of the highest save counter seen for each vault, so a whole-file rollback
/// can be reported at unlock (THREAT-MODEL.md A2). DPAPI-protected, current user.
/// </summary>
public interface IRollbackGuard
{
    /// <summary>Highest counter recorded for a vault, or <see langword="null"/> when it is unknown.</summary>
    /// <param name="vaultIdHex">Stable identifier of the vault, hex encoded.</param>
    ulong? LastSeenCounter(string vaultIdHex);

    /// <summary>Records a counter; a lower value than the stored one is ignored.</summary>
    /// <param name="vaultIdHex">Stable identifier of the vault, hex encoded.</param>
    /// <param name="counter">Save counter just observed.</param>
    void Record(string vaultIdHex, ulong counter);
}

/// <summary>Cut/copy/paste inside the vault. Vault content never reaches the OS clipboard.</summary>
public interface IInternalClipboard
{
    /// <summary>What is on the clipboard, or <see langword="null"/> when it is empty.</summary>
    ClipboardOp? Content { get; }

    /// <summary>Puts entries on the clipboard.</summary>
    /// <param name="ids">Entries that were cut or copied.</param>
    /// <param name="isCut">True for a cut.</param>
    /// <param name="sourceVaultPath">Vault the entries belong to.</param>
    void Set(IReadOnlyList<EntryId> ids, bool isCut, string sourceVaultPath);

    /// <summary>Empties the clipboard (also called on lock).</summary>
    void Clear();

    /// <summary>Raised whenever the content changes.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// The OS clipboard, used only for "Copy path" and "Copy details" text. Everything written is
/// tagged so clipboard history and cloud sync leave it alone (UI-CONTRACT.md section 1.11).
/// </summary>
public interface IOsClipboard
{
    /// <summary>Writes text, excluded from clipboard history and monitor processing.</summary>
    /// <param name="text">The text to place on the clipboard.</param>
    void SetText(string text);

    /// <summary>Reads a file drop list, or <see langword="null"/> when the clipboard holds none.</summary>
    IReadOnlyList<string>? GetFileDropList();

    /// <summary>True when the clipboard currently holds a file drop list.</summary>
    bool HasFileDrop { get; }
}

/// <summary>System-wide idle time, polled with <c>GetLastInputInfo</c>.</summary>
public interface IIdleMonitor
{
    /// <summary>How long the machine has been idle.</summary>
    TimeSpan Idle { get; }

    /// <summary>Raised once each time the idle time passes <see cref="Threshold"/>.</summary>
    event EventHandler? IdleThresholdReached;

    /// <summary>Idle time that triggers the event.</summary>
    TimeSpan Threshold { get; set; }

    /// <summary>Turns polling on and off.</summary>
    bool Enabled { get; set; }
}

/// <summary>Session and power events that must lock the vault immediately.</summary>
public interface ISystemEvents
{
    /// <summary>The workstation was locked, or a remote/console session was disconnected.</summary>
    event EventHandler? SessionLocked;

    /// <summary>The machine is going to sleep or hibernating.</summary>
    event EventHandler? Suspending;

    /// <summary>The user is logging off or the machine is shutting down.</summary>
    event EventHandler? SessionEnding;
}

/// <summary>Windows shell integration. Registration happens only from the Settings dialog.</summary>
public interface IShellIntegration
{
    /// <summary>Registers the <c>.bastion</c> file type for the current user (HKCU).</summary>
    void RegisterFileAssociation();

    /// <summary>Removes the <c>.bastion</c> registration for the current user.</summary>
    void UnregisterFileAssociation();

    /// <summary>True when this executable is currently registered for <c>.bastion</c>.</summary>
    bool IsRegistered { get; }

    /// <summary>Sets the AppUserModelID and turns the automatic jump list off; called once at start-up.</summary>
    void ApplyProcessHygiene();
}

/// <summary>Keeps one vault open in exactly one process.</summary>
public interface ISingleInstance
{
    /// <summary>
    /// Takes the per-vault lock; returns <see langword="null"/> when another process already holds it.
    /// Dispose the returned handle to release the lock.
    /// </summary>
    /// <param name="path">Full path of the vault.</param>
    IDisposable? TryAcquireVault(string path);

    /// <summary>Asks the process that holds the vault to come to the front.</summary>
    /// <param name="path">Full path of the vault.</param>
    void FocusExistingInstance(string path);
}

/// <summary>Keeps the window out of screen captures (<c>SetWindowDisplayAffinity</c>).</summary>
public interface IScreenPrivacy
{
    /// <summary>Turns capture exclusion on or off for the shell window.</summary>
    /// <param name="exclude">True to exclude the window from capture.</param>
    void SetExcludeFromCapture(bool exclude);
}

/// <summary>Marshals work onto the UI thread. The only dispatcher a view model may see.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues an action on the UI thread at background priority.</summary>
    /// <param name="action">Work to run.</param>
    void Post(Action action);

    /// <summary>
    /// Runs an action on the UI thread now and waits for it. For the two notifications that get
    /// no second chance - <see cref="ISystemEvents.Suspending"/> and
    /// <see cref="ISystemEvents.SessionEnding"/> - Windows gives the process a short window
    /// before it suspends or terminates it, and a background-priority queue item is the last
    /// thing to run, so the vault would go into the hibernation file with its keys unzeroed.
    /// Everything else keeps <see cref="Post"/>.
    /// </summary>
    /// <param name="action">Work to run.</param>
    void Send(Action action);

    /// <summary>
    /// Queues an action to run on the UI thread after a delay, at background priority. This is
    /// the 80 ms <c>DispatcherTimer</c> of UI-CONTRACT.md section 1.2, kept behind the seam so
    /// <see cref="ThrottledProgress{T}"/> stays testable without an STA thread.
    /// </summary>
    /// <param name="delay">How long to wait before running.</param>
    /// <param name="action">Work to run.</param>
    /// <returns>A handle that cancels the pending run when disposed.</returns>
    IDisposable PostDelayed(TimeSpan delay, Action action);

    /// <summary>True when the caller is already on the UI thread.</summary>
    bool CheckAccess();
}

/// <summary>
/// The rolling text log under <c>%LOCALAPPDATA%\Bastion\logs</c>. It never receives an entry name,
/// an in-vault path, a key, a salt or an id (UI-CONTRACT.md section 1.13).
/// </summary>
public interface ILog
{
    /// <summary>Records an ordinary event.</summary>
    /// <param name="message">Message text; no vault content.</param>
    void Info(string message);

    /// <summary>Records something unexpected that the app recovered from.</summary>
    /// <param name="message">Message text; no vault content.</param>
    /// <param name="ex">Optional exception.</param>
    void Warn(string message, Exception? ex = null);

    /// <summary>Records a failure.</summary>
    /// <param name="message">Message text; no vault content.</param>
    /// <param name="ex">Optional exception.</param>
    void Error(string message, Exception? ex = null);
}

/// <summary>Estimates how long a key derivation will take on this machine; results are cached.</summary>
public interface IKdfEstimator
{
    /// <summary>Estimates the wall-clock cost of one derivation at <paramref name="parameters"/>.</summary>
    /// <param name="parameters">Argon2id parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TimeSpan> EstimateAsync(KdfParameters parameters, CancellationToken ct);
}
