namespace BastionVault.Core;

/// <summary>
/// An open vault. One operation at a time: long operations take the session lock and a concurrent
/// call throws <see cref="VaultErrorCode.Busy"/> instead of queueing. Snapshot reads are cheap,
/// synchronous and thread-safe against a running operation. No member ever touches a
/// <see cref="System.Threading.SynchronizationContext"/>.
/// </summary>
public interface IVaultSession : IAsyncDisposable
{
    /// <summary>Absolute path of the vault file backing this session.</summary>
    string Path { get; }

    /// <summary>
    /// The derived <c>vaultId</c> (FORMAT.md section 2.4) as 32 lowercase hex characters: the key of
    /// local per-machine records. It is not key material - it is an HKDF label expansion the file never
    /// stores - so a locked session still returns the value captured while it was unlocked. It changes
    /// when the vault key does: after a <see cref="CredentialChangeMode.Rekey"/> change has been saved.
    /// </summary>
    string VaultIdHex { get; }

    /// <summary>True when the session was opened read-only; mutations throw <see cref="VaultErrorCode.ReadOnlySession"/>.</summary>
    bool IsReadOnly { get; }

    /// <summary>True while the keys are zeroed (FORMAT.md section 8.8). The tree and staged data survive a lock.</summary>
    bool IsLocked { get; }

    /// <summary>True when the session holds changes that a save would commit.</summary>
    bool IsDirty { get; }

    /// <summary>True while a long operation holds the session lock.</summary>
    bool IsBusy { get; }

    /// <summary>Argon2id parameters currently stored in the header (a pending change is not reflected here until it is saved).</summary>
    KdfParameters Kdf { get; }

    /// <summary>Aggregate numbers about the vault, computed from the in-memory tree.</summary>
    VaultStatistics Statistics { get; }

    /// <summary>Summary of everything a save would commit.</summary>
    PendingChanges Pending { get; }

    /// <summary>True when <see cref="UndoAsync"/> would do something.</summary>
    bool CanUndo { get; }

    /// <summary>True when <see cref="RedoAsync"/> would do something.</summary>
    bool CanRedo { get; }

    /// <summary>Human-readable description of the next undo step, or <see langword="null"/>.</summary>
    string? UndoDescription { get; }

    /// <summary>Human-readable description of the next redo step, or <see langword="null"/>.</summary>
    string? RedoDescription { get; }

    /// <summary>Raised after every mutation, save, lock/unlock and dirty transition. May fire on any thread; the App marshals.</summary>
    event EventHandler<VaultChangedEventArgs>? Changed;

    /// <summary>Direct children of a folder: folders first, then files, in natural order by name.</summary>
    /// <param name="folder">Folder to list; <see cref="EntryId.Root"/> for the top level.</param>
    IReadOnlyList<EntryInfo> GetChildren(EntryId folder);

    /// <summary>Returns the snapshot of an entry, or <see langword="null"/> when the id is unknown.</summary>
    /// <param name="id">Entry to look up.</param>
    EntryInfo? Find(EntryId id);

    /// <summary>Returns the chain from the top-level ancestor down to the entry itself; the root is excluded.</summary>
    /// <param name="id">Entry to describe.</param>
    IReadOnlyList<EntryInfo> GetAncestors(EntryId id);

    /// <summary>Formats the in-vault path of an entry: a single separator for the root, otherwise the full path.</summary>
    /// <param name="id">Entry to format.</param>
    string FormatPath(EntryId id);

    /// <summary>Resolves a separator-delimited in-vault path case-insensitively.</summary>
    /// <param name="vaultPath">Path to resolve, for example <c>\Documents\2026\notes.txt</c>.</param>
    /// <param name="id">The entry found, or <see cref="EntryId.Root"/> when the path does not resolve.</param>
    /// <returns>True when the path resolves to an existing entry.</returns>
    bool TryResolvePath(string vaultPath, out EntryId id);

    /// <summary>Checks a name against FORMAT.md section 6.1 and against the siblings of <paramref name="parent"/>.</summary>
    /// <param name="parent">Folder the name would live in.</param>
    /// <param name="name">Candidate name.</param>
    /// <param name="ignoring">Entry to ignore during the uniqueness check (the entry being renamed).</param>
    NameCheck ValidateName(EntryId parent, string name, EntryId? ignoring = null);

    /// <summary>Case-insensitive substring search over entry names.</summary>
    /// <param name="nameSubstring">Text to look for.</param>
    /// <param name="scope">Subtree to search, or <see langword="null"/> for the whole vault.</param>
    /// <param name="maxResults">Maximum number of hits to return.</param>
    /// <param name="ct">Cancellation token.</param>
    IReadOnlyList<EntryInfo> Search(string nameSubstring, EntryId? scope, int maxResults, CancellationToken ct);

    /// <summary>Creates an empty folder. In memory: pushes an undo step, raises <see cref="Changed"/> and marks the session dirty.</summary>
    /// <param name="parent">Destination folder.</param>
    /// <param name="name">Name of the new folder.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task<EntryId> CreateFolderAsync(EntryId parent, string name, CancellationToken ct);

    /// <summary>Renames an entry.</summary>
    /// <param name="entry">Entry to rename.</param>
    /// <param name="newName">New name, validated per FORMAT.md section 6.1.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task RenameAsync(EntryId entry, string newName, CancellationToken ct);

    /// <summary>Replaces an entry's comment (0 .. 4096 UTF-8 bytes).</summary>
    /// <param name="entry">Entry to annotate.</param>
    /// <param name="comment">New comment text.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task SetCommentAsync(EntryId entry, string comment, CancellationToken ct);

    /// <summary>
    /// Moves entries to another folder. Moving an entry into itself, into one of its descendants, or moving
    /// the root throws <see cref="VaultErrorCode.InvalidMove"/>.
    /// </summary>
    /// <param name="entries">Entries to move.</param>
    /// <param name="newParent">Destination folder.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task MoveAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct);

    /// <summary>Copies entries including their content; the copies are re-encrypted under fresh blob ids at the next save.</summary>
    /// <param name="entries">Entries to copy.</param>
    /// <param name="newParent">Destination folder.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    /// <returns>The ids of the new top-level copies.</returns>
    Task<IReadOnlyList<EntryId>> CopyAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct);

    /// <summary>Deletes entries and their subtrees from the in-memory tree.</summary>
    /// <param name="entries">Entries to delete.</param>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task DeleteAsync(IReadOnlyList<EntryId> entries, CancellationToken ct);

    /// <summary>Undoes the last tree mutation.</summary>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task UndoAsync(CancellationToken ct);

    /// <summary>Redoes the last undone tree mutation.</summary>
    /// <param name="ct">Cancellation token; checked before work starts.</param>
    Task RedoAsync(CancellationToken ct);

    /// <summary>
    /// Imports files and folders from disk into <paramref name="parent"/>. Continue-on-error with a report;
    /// content is encrypted straight into staging, plaintext never touches disk. Cancelling discards this
    /// import's staged blobs as a unit and leaves the tree unchanged.
    /// </summary>
    /// <param name="parent">Destination folder.</param>
    /// <param name="sourcePaths">Files and directories to import.</param>
    /// <param name="options">Conflict handling, timestamps and depth limit.</param>
    /// <param name="progress">Optional progress sink; reports are rate-limited at the source.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ImportResult> ImportAsync(EntryId parent, IReadOnlyList<string> sourcePaths, ImportOptions options,
                                   IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Exports entries to a directory on disk (FORMAT.md section 8.7). Continue-on-error with a report;
    /// cancelling deletes the partial output file while files already completed remain.
    /// </summary>
    /// <param name="entries">Entries to export.</param>
    /// <param name="destinationDirectory">Export root; every written path must stay inside it.</param>
    /// <param name="options">Conflict handling, timestamps and Mark of the Web.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ExportResult> ExportAsync(IReadOnlyList<EntryId> entries, string destinationDirectory, ExportOptions options,
                                   IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Opens a forward-only decrypting stream over a file (stored or pending). Each chunk is
    /// authenticated before its bytes are returned.
    /// </summary>
    /// <param name="file">File entry to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="VaultIntegrityException"><see cref="VaultErrorCode.DataCorrupt"/> when a chunk tag fails.</exception>
    Task<Stream> OpenReadAsync(EntryId file, CancellationToken ct);

    /// <summary>Authenticates every blob and checks the layout; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<VerifyReport> VerifyAsync(IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>Best-effort export of everything that still authenticates, optionally writing partial files.</summary>
    /// <param name="destinationDirectory">Export root.</param>
    /// <param name="options">Export settings; <see cref="ExportOptions.WritePartialFiles"/> applies here.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ExportResult> RecoverAsync(string destinationDirectory, ExportOptions options,
                                    IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Commits every pending change by rewriting the whole file (FORMAT.md section 8.3). Until the
    /// <c>File.Replace</c> step a cancellation deletes the temp file and leaves the vault untouched;
    /// after it the token is ignored.
    /// </summary>
    /// <param name="options">Save settings.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(SaveOptions options, IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Writes a complete, re-keyed copy of the current state to <paramref name="newPath"/>. Always uses
    /// <see cref="SaveMode.Rekey"/> so the two files never share a key space; the session keeps editing the original.
    /// </summary>
    /// <param name="newPath">Path of the copy.</param>
    /// <param name="password">Password protecting the copy.</param>
    /// <param name="keyFile">Optional keyfile for the copy.</param>
    /// <param name="kdf">Argon2id parameters for the copy.</param>
    /// <param name="options">Save settings.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveCopyAsync(string newPath, Passphrase password, KeyFile? keyFile, KdfParameters kdf, SaveOptions options,
                       IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Derives the new KEK now and records the change as pending; the next <see cref="SaveAsync"/> is the
    /// single commit point. The KDF phase is not interruptible.
    /// </summary>
    /// <param name="newPassword">The new password.</param>
    /// <param name="newKeyFile">The new keyfile, or <see langword="null"/> to use none.</param>
    /// <param name="kdf">Argon2id parameters to store.</param>
    /// <param name="mode">Re-key (new vault key and blob ids) or rewrap only.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ChangeCredentialsAsync(Passphrase newPassword, KeyFile? newKeyFile, KdfParameters kdf, CredentialChangeMode mode,
                                IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>Drops all pending edits, staged data, the pending credential change and the undo stack.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task DiscardChangesAsync(CancellationToken ct);

    /// <summary>
    /// Zeroes the vault key, index key, all blob keys and pending credential material, and discards a pending
    /// credential change. The tree and staged ciphertext are kept. Synchronous, idempotent, never throws.
    /// </summary>
    void Lock();

    /// <summary>
    /// Re-derives the KEK, unwraps the vault key from the header and verifies that the derived vault id
    /// equals the session's vault id.
    /// </summary>
    /// <param name="password">The vault password.</param>
    /// <param name="keyFile">The keyfile, when one is used.</param>
    /// <param name="progress">Optional progress sink; the KDF phase reports <c>IsCancellable = false</c>.</param>
    /// <param name="ct">Cancellation token; honoured right after the KDF returns.</param>
    /// <exception cref="VaultAuthenticationException"><see cref="VaultErrorCode.AuthenticationFailed"/> for a wrong password or keyfile, or an altered header.</exception>
    Task UnlockAsync(Passphrase password, KeyFile? keyFile, IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Checks a password (and keyfile) against the credentials currently stored in the header, without
    /// changing a single byte of session state. It re-derives the KEK from the header's salt and Argon2id
    /// parameters, tries the key unwrap and compares the resulting vault id with the session's. Used by the
    /// Change credentials dialog to confirm the current password before a new one is accepted.
    /// </summary>
    /// <remarks>
    /// The call takes the session lock like any other long operation, so it throws
    /// <see cref="VaultOperationException"/> with <see cref="VaultErrorCode.Busy"/> while one is running.
    /// A pending credential change is ignored: the header on disk is what "current" means until the next
    /// save. Works on a locked session too. The KDF phase is not interruptible.
    /// </remarks>
    /// <param name="password">The password to check.</param>
    /// <param name="keyFile">The keyfile to check with, or <see langword="null"/> for none.</param>
    /// <param name="ct">Cancellation token; honoured right after the KDF returns.</param>
    /// <returns><see langword="true"/> when the password and keyfile open this vault.</returns>
    Task<bool> VerifyPasswordAsync(Passphrase password, KeyFile? keyFile, CancellationToken ct);

    /// <summary>Alias of <see cref="Lock"/> for crash handlers.</summary>
    void ZeroKeys();
}
