namespace BastionVault.Core;

/// <summary>
/// Immutable snapshot of a single entry. Core never hands out mutable domain objects.
/// </summary>
/// <param name="Id">Stable id of the entry.</param>
/// <param name="ParentId">Id of the containing folder; <see cref="EntryId.Root"/> for a top-level entry.</param>
/// <param name="Kind">Folder or file.</param>
/// <param name="Name">Entry name, valid per FORMAT.md §6.1.</param>
/// <param name="Length">File: plaintext bytes. Folder: recursive total (cached rollup).</param>
/// <param name="ChildCount">Folder: number of direct children. File: 0.</param>
/// <param name="CreatedUtc">Creation timestamp (UTC).</param>
/// <param name="ModifiedUtc">Last modification timestamp (UTC).</param>
/// <param name="Comment">Free-text comment, up to 4096 UTF-8 bytes.</param>
/// <param name="State">State relative to the last successful save.</param>
public sealed record EntryInfo(
    EntryId Id,
    EntryId ParentId,
    EntryKind Kind,
    string Name,
    long Length,
    int ChildCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    string Comment,
    EntryState State);

/// <summary>Aggregate numbers about the open vault.</summary>
/// <param name="FolderCount">Number of folders in the tree.</param>
/// <param name="FileCount">Number of files in the tree.</param>
/// <param name="TotalPlaintextBytes">Sum of all file plaintext lengths.</param>
/// <param name="OnDiskBytes">Current length of the vault file on disk.</param>
/// <param name="SaveCounter">Save counter from the index (1 at creation, +1 per successful save).</param>
/// <param name="LastSavedUtc">Timestamp recorded by the last save, or <see langword="null"/> when unknown.</param>
/// <param name="OpenedFromIndexCopy">True when the primary index failed to authenticate and the copy was used ("save to repair").</param>
public sealed record VaultStatistics(
    int FolderCount,
    int FileCount,
    long TotalPlaintextBytes,
    long OnDiskBytes,
    ulong SaveCounter,
    DateTimeOffset? LastSavedUtc,
    bool OpenedFromIndexCopy);

/// <summary>Summary of everything that a save would commit.</summary>
/// <param name="Added">Entries added since the last save.</param>
/// <param name="Changed">Entries renamed, moved or comment-edited since the last save.</param>
/// <param name="Deleted">Entries deleted since the last save.</param>
/// <param name="BytesToWrite">Estimated plaintext bytes that the save must write.</param>
/// <param name="CredentialChangePending">A password, keyfile or KDF change is waiting for the next save.</param>
/// <param name="RekeyPending">The next save will re-key (fresh vault key and blob ids).</param>
public sealed record PendingChanges(
    int Added,
    int Changed,
    int Deleted,
    long BytesToWrite,
    bool CredentialChangePending,
    bool RekeyPending)
{
    /// <summary>True when the session has anything to commit.</summary>
    public bool Any => Added + Changed + Deleted > 0 || CredentialChangePending;
}

/// <summary>
/// One progress report. A <see langword="readonly record struct"/> so reporting does not allocate.
/// Rate-limited at the source to one report per <c>max(4 MiB, 1 % of BytesTotal)</c> plus one at start and one at completion.
/// </summary>
/// <param name="Operation">Operation being reported.</param>
/// <param name="BytesDone">Bytes processed so far.</param>
/// <param name="BytesTotal">Total bytes to process, or 0 when unknown.</param>
/// <param name="ItemsDone">Items completed so far.</param>
/// <param name="ItemsTotal">Total items, or 0 when unknown.</param>
/// <param name="CurrentItem">Name or path of the item currently being processed.</param>
/// <param name="IsCancellable">False while the operation cannot honour the token (KDF phase, post-<c>File.Replace</c>).</param>
public readonly record struct VaultProgress(
    VaultOperation Operation,
    long BytesDone,
    long BytesTotal,
    int ItemsDone,
    int ItemsTotal,
    string? CurrentItem,
    bool IsCancellable);

/// <summary>Payload of <see cref="IVaultSession.Changed"/>. May be raised on any thread.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Affected">Ids affected by the change (empty for session-wide notifications).</param>
/// <param name="Parent">Parent folder the change relates to, or <see cref="EntryId.Root"/>.</param>
public sealed record VaultChangedEventArgs(VaultChangeKind Kind, IReadOnlyList<EntryId> Affected, EntryId Parent);

/// <summary>Result of validating a name for a given parent folder.</summary>
/// <param name="IsValid">True when the name may be used as-is.</param>
/// <param name="Reason">Human-readable reason when invalid, otherwise <see langword="null"/>.</param>
/// <param name="Suggestion">A valid alternative the UI may offer, otherwise <see langword="null"/>.</param>
public sealed record NameCheck(bool IsValid, string? Reason, string? Suggestion)
{
    /// <summary>The successful result: valid, no reason, no suggestion.</summary>
    public static readonly NameCheck Ok = new(true, null, null);
}
