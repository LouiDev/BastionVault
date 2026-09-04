namespace BastionVault.Core;

/// <summary>Kind of a vault entry.</summary>
public enum EntryKind : byte
{
    /// <summary>A folder; may contain other entries.</summary>
    Folder = 0,

    /// <summary>A file; carries content stored as one blob.</summary>
    File = 1,
}

/// <summary>
/// Persistence state of an entry relative to the last successful save.
/// </summary>
public enum EntryState
{
    /// <summary>Unchanged since the last save.</summary>
    Stored,

    /// <summary>New since the last save (import, copy, new folder).</summary>
    Added,

    /// <summary>Renamed, moved or comment-edited since the last save.</summary>
    Changed,
}

/// <summary>Long-running operation reported through <see cref="VaultProgress"/>.</summary>
public enum VaultOperation
{
    /// <summary>Opening an existing vault file.</summary>
    Open,

    /// <summary>Creating a new vault file.</summary>
    Create,

    /// <summary>Saving the session back to its vault file.</summary>
    Save,

    /// <summary>Writing a re-keyed copy of the vault to a new path.</summary>
    SaveCopy,

    /// <summary>Importing files or folders from disk.</summary>
    Import,

    /// <summary>Exporting entries to disk.</summary>
    Export,

    /// <summary>Verifying every blob against its authentication tags and commitment hash.</summary>
    Verify,

    /// <summary>Best-effort export of a damaged vault.</summary>
    Recover,

    /// <summary>Changing password, keyfile or KDF parameters.</summary>
    ChangeCredentials,

    /// <summary>Copying entries inside the vault.</summary>
    Copy,

    /// <summary>The Argon2id key-derivation phase (not cancellable).</summary>
    KeyDerivation,
}

/// <summary>How a save rewrites the data section (FORMAT.md §8.4).</summary>
public enum SaveMode
{
    /// <summary>Default: the vault key is unchanged and blobs are copied verbatim by byte range.</summary>
    Compact,

    /// <summary>A fresh vault key: every blob is streamed decrypt → encrypt under a fresh blob id.</summary>
    Rekey,
}

/// <summary>How a credential change is applied at the next save (FORMAT.md §8.4).</summary>
public enum CredentialChangeMode
{
    /// <summary>New vault key, new salt, new wrap nonce, new vault id and new blob ids (default for a password change).</summary>
    Rekey,

    /// <summary>Fast change: new salt and wrap nonce only, the vault key is kept.</summary>
    RewrapOnly,
}

/// <summary>What to do when an imported or exported name already exists at the destination.</summary>
public enum ConflictPolicy
{
    /// <summary>Give the incoming item a unique name (<c>name (2).ext</c>).</summary>
    Rename,

    /// <summary>Overwrite the existing item.</summary>
    Replace,

    /// <summary>Leave the existing item alone and skip the incoming one.</summary>
    Skip,
}

/// <summary>Answer of an interactive conflict resolver.</summary>
public enum ConflictDecision
{
    /// <summary>Rename this item.</summary>
    Rename,

    /// <summary>Replace this item.</summary>
    Replace,

    /// <summary>Skip this item.</summary>
    Skip,

    /// <summary>Rename this and every following conflict.</summary>
    RenameAll,

    /// <summary>Replace this and every following conflict.</summary>
    ReplaceAll,

    /// <summary>Skip this and every following conflict.</summary>
    SkipAll,

    /// <summary>Abort the whole operation.</summary>
    Cancel,
}

/// <summary>Why an item was not imported exactly as it appeared on disk.</summary>
public enum ImportIssueKind
{
    /// <summary>A junction or symlink was skipped (reparse points are never followed).</summary>
    SkippedReparsePoint,

    /// <summary>The source was locked by another process.</summary>
    Locked,

    /// <summary>The source could not be read.</summary>
    Unreadable,

    /// <summary>The disk name had to be sanitised or made unique (FORMAT.md §6.2).</summary>
    Renamed,

    /// <summary>Length or modification time changed while the source was being read; the entry was dropped.</summary>
    ChangedWhileReading,

    /// <summary>The source tree exceeded the configured maximum depth.</summary>
    TooDeep,

    /// <summary>The import was cancelled before this item was staged.</summary>
    Cancelled,

    /// <summary>The item was deliberately not imported: a name conflict was resolved as "skip".</summary>
    Skipped,
}

/// <summary>Why an entry was not exported exactly as it is stored.</summary>
public enum ExportIssueKind
{
    /// <summary>The destination name had to be changed to avoid a conflict or an invalid disk name.</summary>
    Renamed,

    /// <summary>A chunk failed authentication; the partial output was deleted.</summary>
    IntegrityFailure,

    /// <summary>The destination could not be written.</summary>
    IoError,

    /// <summary>The destination path exceeded the platform limit.</summary>
    PathTooLong,

    /// <summary>The destination is a reparse point and was refused.</summary>
    ReparsePointRefused,

    /// <summary>The entry was skipped by the conflict policy.</summary>
    Skipped,

    /// <summary>Recover only: the authenticated prefix was written as <c>name.partial</c>.</summary>
    PartialWritten,
}

/// <summary>What changed in a session, carried by <see cref="VaultChangedEventArgs"/>.</summary>
public enum VaultChangeKind
{
    /// <summary>Entries were added under <see cref="VaultChangedEventArgs.Parent"/>.</summary>
    EntriesAdded,

    /// <summary>Entries were deleted.</summary>
    EntriesRemoved,

    /// <summary>An entry was renamed.</summary>
    EntryRenamed,

    /// <summary>Entries were moved to a new parent.</summary>
    EntriesMoved,

    /// <summary>An entry's metadata (for example its comment) changed.</summary>
    EntryUpdated,

    /// <summary>The whole tree was replaced (open, discard, undo of a bulk operation).</summary>
    Reloaded,

    /// <summary><see cref="IVaultSession.IsDirty"/> flipped.</summary>
    DirtyChanged,

    /// <summary><see cref="IVaultSession.IsLocked"/> flipped.</summary>
    LockChanged,

    /// <summary>A save completed successfully.</summary>
    Saved,
}
