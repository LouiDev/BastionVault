# BastionVault.Core public API (frozen contract)

`BastionVault.Core` (net10.0, no UI dependencies) exposes the surface below. The App and
the tests compile against it. Changes to this file go through the orchestrator only.
The skeleton in `src/BastionVault.Core` mirrors this file one-to-one.

## Rules

1. **Core never touches a SynchronizationContext.** Every `await` in Core uses
   `ConfigureAwait(false)`. `Changed` may be raised on any thread; the App marshals.
2. **Core returns immutable snapshots** (`EntryInfo` records, read-only lists). It never
   exposes observable collections or mutable domain objects.
3. **One operation at a time per session.** Long operations take the session lock
   (`SemaphoreSlim(1,1)`); a concurrent call throws `VaultOperationException(Busy)`, it
   is never queued. Cheap snapshot reads (`GetChildren`, `Find`, …) are always allowed
   and are thread-safe against a concurrent operation (they read a consistent snapshot).
4. **Secrets are never `string`.** `Passphrase` and `KeyFile` are `IDisposable`; disposing
   zeroes. All key buffers are pinned (`GC.AllocateArray<byte>(n, pinned: true)`) and
   zeroed with `CryptographicOperations.ZeroMemory` when released.
5. **Errors are exceptions**, always a `VaultException` subclass carrying a
   `VaultErrorCode`, or `OperationCanceledException`. No raw `IOException`,
   `CryptographicException` or `ArgumentOutOfRangeException` leaves `BastionVault.Core`
   (they are wrapped as `IoError` / `IndexInvalid` etc.). Argument misuse by the caller
   (null, wrong id) still throws the usual `ArgumentException` family.
6. **Progress is rate-limited at the source:** at most one report per
   `max(4 MiB, 1 % of BytesTotal)` plus one at start and one at completion.
   The transition into a phase that no longer honours the token is a state change, not a
   byte count: the first report with `IsCancellable = false` always gets through, whatever
   the throttle would say, so the App can take Cancel away at the right moment.
   `VaultProgress` is a `readonly record struct` (no allocation per report).
7. **EntryId is stable for the lifetime of the vault.** A save never renumbers; ids are
   never reused. The App may cache ids across saves.
8. **Determinism seams**: `IRandomSource`, `IClock`, `IVaultPaths` are the only places
   Core touches randomness, time and file naming. `InternalsVisibleTo("BastionVault.Core.Tests")`.
9. **`VaultIdHex` is the identity of a key space, not of a file.** It is the derived `vaultId` of
   FORMAT.md §2.4 (`HKDF-Expand(VaultKey, "bastion/v1/vaultid", 16)`) as 32 lowercase hex characters,
   and it is what local per-machine records — the rollback counter, anything else the host keeps — are
   keyed on, never the path. It is not key material and is never stored in the file, so a locked
   session still returns the value captured while it was unlocked; it changes only when the vault key
   does, that is after a `CredentialChangeMode.Rekey` change has been saved (`SaveCopyAsync` writes a
   new key space to the copy and leaves the session's id alone).

## Cancellation semantics (one row per operation)

| Operation            | Cancel leaves behind                                                   |
|----------------------|-----------------------------------------------------------------------|
| Open / Unlock        | Nothing. The KDF phase itself is not interruptible; the token is honoured right after it returns (result zeroed). `IsCancellable=false` in progress during KDF. |
| Create               | No file (temp deleted).                                               |
| Import               | That import's staged blobs are discarded; the tree is unchanged.      |
| Export / Recover     | The partial output file is deleted; files already completed remain.   |
| Verify               | Partial report is returned via `OperationCanceledException.Data["Report"]`? No: Verify throws `OperationCanceledException`; the App shows "cancelled". |
| Save / SaveCopy      | Temp deleted, vault untouched, session state unchanged — until step 6 of the state machine (`File.Replace`), after which cancellation is ignored and progress reports `IsCancellable=false` (that one report is never throttled away). A concurrent `Lock()` is reported as `SessionLocked`, never as `SaveVerificationFailed`. |
| ChangeCredentials    | Not applied (it is pending until Save). KDF phase is not interruptible. |
| VerifyPassword       | Nothing; it never changes state. KDF phase is not interruptible; the token is honoured right after it returns. |
| Copy / Move / Rename / Delete / CreateFolder | Synchronous in effect; the token is only checked before work starts. |

## C# surface

```csharp
namespace BastionVault.Core;

// ───────────── identities & enums ─────────────
public readonly record struct EntryId(uint Value)
{
    public static readonly EntryId Root = new(0);
    public bool IsRoot => Value == 0;
}

public enum EntryKind : byte { Folder = 0, File = 1 }

/// Stored = unchanged since last save; Added = new since last save (import/copy/new folder);
/// Changed = renamed, moved or comment-edited since last save.
public enum EntryState { Stored, Added, Changed }

public enum VaultOperation { Open, Create, Save, SaveCopy, Import, Export, Verify, Recover, ChangeCredentials, Copy, KeyDerivation }
public enum SaveMode { Compact, Rekey }
public enum CredentialChangeMode { Rekey, RewrapOnly }
public enum KdfPreset { Fast, Standard, Strong }
public enum ConflictPolicy { Rename, Replace, Skip }
public enum ConflictDecision { Rename, Replace, Skip, RenameAll, ReplaceAll, SkipAll, Cancel }
public enum ImportIssueKind { SkippedReparsePoint, Locked, Unreadable, Renamed, ChangedWhileReading, TooDeep, Cancelled, Skipped }
public enum ExportIssueKind { Renamed, IntegrityFailure, IoError, PathTooLong, ReparsePointRefused, Skipped, PartialWritten }
public enum VaultChangeKind { EntriesAdded, EntriesRemoved, EntryRenamed, EntriesMoved, EntryUpdated, Reloaded, DirtyChanged, LockChanged, Saved }

// ───────────── snapshots ─────────────
public sealed record EntryInfo(
    EntryId Id,
    EntryId ParentId,
    EntryKind Kind,
    string Name,
    long Length,                    // file: plaintext bytes; folder: recursive total (cached rollup)
    int ChildCount,                 // folder: direct children; file: 0
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    string Comment,
    EntryState State);

public sealed record VaultStatistics(
    int FolderCount, int FileCount, long TotalPlaintextBytes, long OnDiskBytes,
    ulong SaveCounter, DateTimeOffset? LastSavedUtc, bool OpenedFromIndexCopy);

public sealed record PendingChanges(
    int Added, int Changed, int Deleted, long BytesToWrite,
    bool CredentialChangePending, bool RekeyPending)
{
    public bool Any => Added + Changed + Deleted > 0 || CredentialChangePending;
}

public readonly record struct VaultProgress(
    VaultOperation Operation, long BytesDone, long BytesTotal,
    int ItemsDone, int ItemsTotal, string? CurrentItem, bool IsCancellable);

public sealed record VaultChangedEventArgs(VaultChangeKind Kind, IReadOnlyList<EntryId> Affected, EntryId Parent);

public sealed record NameCheck(bool IsValid, string? Reason, string? Suggestion)
{
    public static readonly NameCheck Ok = new(true, null, null);
}

// ───────────── KDF ─────────────
public sealed record KdfParameters(uint MemoryKiB, uint Iterations, uint Parallelism)
{
    public static KdfParameters FromPreset(KdfPreset preset);          // Fast 64 MiB/3/4 · Standard 512 MiB/3/4 · Strong 1 GiB/4/4
    public static KdfParameters Default => FromPreset(KdfPreset.Standard);
    public KdfPreset? MatchingPreset { get; }                            // null when custom
    public long MemoryBytes => (long)MemoryKiB * 1024;
    public void Validate();                                              // throws VaultFormatException(UnsupportedParameters) per FORMAT.md §7
    public bool IsValid { get; }
}

public sealed record VaultHeaderInfo(
    ushort FormatVersion, KdfParameters Kdf, long FileLength, long IndexLength, long RequiredMemoryBytes);

public static class KdfBenchmark
{
    /// Measures a small Argon2id run on this machine and scales to `parameters`.
    public static Task<TimeSpan> EstimateAsync(KdfParameters parameters, CancellationToken ct);
}

// ───────────── secrets ─────────────
public sealed class Passphrase : IDisposable
{
    public static Passphrase FromString(string password);                // NFC + UTF-8, validates 1..1024 bytes
    public static Passphrase FromChars(ReadOnlySpan<char> password);     // same, without materialising a string when ASCII
    public ReadOnlySpan<byte> Bytes { get; }
    public int Length { get; }
    public Passphrase Clone();
    public void Dispose();                                               // zeroes
}

public sealed class KeyFile : IDisposable
{
    public static KeyFile Load(string path);                             // 1 B .. 1 MiB, else VaultFormatException(UnsupportedParameters)
    public static KeyFile FromBytes(ReadOnlySpan<byte> content);
    public static byte[] GenerateContent(int length = 64, IRandomSource? random = null);
    public string? SourcePath { get; }
    public ReadOnlySpan<byte> Digest { get; }                            // 32 bytes, FORMAT.md §2.2
    public void Dispose();
}

// ───────────── options & results ─────────────
public sealed record ImportOptions(
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    Func<ConflictContext, CancellationToken, ValueTask<ConflictDecision>>? ConflictResolver = null,
    bool PreserveTimestamps = true,
    int MaxDepth = 128);

public sealed record ConflictContext(EntryId Parent, string Name, EntryInfo Existing, string SourcePath, long SourceLength);

public sealed record ImportIssue(string SourcePath, ImportIssueKind Kind, string? Detail);
public sealed record ImportResult(IReadOnlyList<EntryId> Imported, long BytesImported, IReadOnlyList<ImportIssue> Issues);

public sealed record ExportOptions(
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    bool RestoreTimestamps = true,
    bool MarkOfTheWeb = true,
    bool WritePartialFiles = false);                                     // Recover only

public sealed record ExportIssue(string VaultPath, ExportIssueKind Kind, string? Detail, uint? ChunkIndex);
public sealed record ExportResult(int FilesWritten, int FoldersCreated, long BytesWritten, IReadOnlyList<ExportIssue> Issues);

public sealed record VerifyFailure(EntryId Id, string VaultPath, uint? ChunkIndex, string Detail);
public sealed record VerifyReport(int FilesChecked, long BytesChecked, TimeSpan Elapsed, bool LayoutOk, IReadOnlyList<VerifyFailure> Failures)
{
    public bool IsClean => LayoutOk && Failures.Count == 0;
}

public sealed record SaveOptions(bool SizeObfuscation = false)
{
    public static readonly SaveOptions Default = new();
}

public sealed record OpenOptions(
    bool ReadOnly = false,
    string? StagingDirectoryOverride = null,
    long InMemoryStagingLimit = 64L * 1024 * 1024)
{
    public static readonly OpenOptions Default = new();
}

// ───────────── session ─────────────
public interface IVaultSession : IAsyncDisposable
{
    string Path { get; }
    string VaultIdHex { get; }                                          // derived vaultId (FORMAT.md §2.4) as 32 lowercase hex chars; survives Lock; changes after a saved Rekey
    bool IsReadOnly { get; }
    bool IsLocked { get; }
    bool IsDirty { get; }
    bool IsBusy { get; }
    KdfParameters Kdf { get; }
    VaultStatistics Statistics { get; }
    PendingChanges Pending { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }

    /// Raised after every mutation, save, lock/unlock and dirty transition. Any thread.
    event EventHandler<VaultChangedEventArgs>? Changed;

    // Snapshot reads — synchronous, cheap, thread-safe, never throw for a valid id.
    IReadOnlyList<EntryInfo> GetChildren(EntryId folder);               // folders first, then files, natural order by name
    EntryInfo? Find(EntryId id);
    IReadOnlyList<EntryInfo> GetAncestors(EntryId id);                  // from the top-level ancestor down to the entry itself (root excluded)
    string FormatPath(EntryId id);                                      // "\" for root, "\Docs\a.txt"
    bool TryResolvePath(string vaultPath, out EntryId id);
    NameCheck ValidateName(EntryId parent, string name, EntryId? ignoring = null);
    IReadOnlyList<EntryInfo> Search(string nameSubstring, EntryId? scope, int maxResults, CancellationToken ct);

    // Tree mutations — in-memory, push an undo step, raise Changed, mark dirty.
    Task<EntryId> CreateFolderAsync(EntryId parent, string name, CancellationToken ct);
    Task RenameAsync(EntryId entry, string newName, CancellationToken ct);
    Task SetCommentAsync(EntryId entry, string comment, CancellationToken ct);
    Task MoveAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct);      // InvalidMove for descendant/self/root
    Task<IReadOnlyList<EntryId>> CopyAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct); // content copy (re-encrypted at save)
    Task DeleteAsync(IReadOnlyList<EntryId> entries, CancellationToken ct);
    Task UndoAsync(CancellationToken ct);
    Task RedoAsync(CancellationToken ct);

    // Content
    Task<ImportResult> ImportAsync(EntryId parent, IReadOnlyList<string> sourcePaths, ImportOptions options,
                                   IProgress<VaultProgress>? progress, CancellationToken ct);
    Task<ExportResult> ExportAsync(IReadOnlyList<EntryId> entries, string destinationDirectory, ExportOptions options,
                                   IProgress<VaultProgress>? progress, CancellationToken ct);
    /// Forward-only decrypting stream over a file (stored or pending). Each chunk is authenticated
    /// before its bytes are returned; a tag failure throws VaultIntegrityException(DataCorrupt).
    Task<Stream> OpenReadAsync(EntryId file, CancellationToken ct);
    Task<VerifyReport> VerifyAsync(IProgress<VaultProgress>? progress, CancellationToken ct);
    Task<ExportResult> RecoverAsync(string destinationDirectory, ExportOptions options,
                                    IProgress<VaultProgress>? progress, CancellationToken ct);

    // Persistence — FORMAT.md §8.3
    Task SaveAsync(SaveOptions options, IProgress<VaultProgress>? progress, CancellationToken ct);
    Task SaveCopyAsync(string newPath, Passphrase password, KeyFile? keyFile, KdfParameters kdf, SaveOptions options,
                       IProgress<VaultProgress>? progress, CancellationToken ct);              // always Rekey; session keeps editing the original
    /// Pending until the next SaveAsync (single commit point). Runs the KDF now to derive the new KEK.
    Task ChangeCredentialsAsync(Passphrase newPassword, KeyFile? newKeyFile, KdfParameters kdf, CredentialChangeMode mode,
                                IProgress<VaultProgress>? progress, CancellationToken ct);
    /// Drops all pending edits, staged data, pending credential change and the undo stack.
    Task DiscardChangesAsync(CancellationToken ct);

    // Lock — FORMAT.md §8.8
    void Lock();                                                        // synchronous, never throws, idempotent
    Task UnlockAsync(Passphrase password, KeyFile? keyFile, IProgress<VaultProgress>? progress, CancellationToken ct);
    /// Checks credentials against the header without touching session state: re-derives the KEK, tries the
    /// unwrap and compares the vault id. Returns false for a wrong password/keyfile instead of throwing.
    /// Takes the session lock (Busy while another operation runs); works locked or unlocked; ignores a
    /// pending credential change (the header is what "current" means until the next save).
    Task<bool> VerifyPasswordAsync(Passphrase password, KeyFile? keyFile, CancellationToken ct);
    void ZeroKeys();                                                    // alias of Lock() for crash handlers
}

public interface IVaultFactory
{
    Task<VaultHeaderInfo> ReadHeaderAsync(string path, CancellationToken ct);       // header checks only, no KDF
    Task<IVaultSession> CreateAsync(string path, Passphrase password, KeyFile? keyFile, KdfParameters kdf,
                                    IProgress<VaultProgress>? progress, CancellationToken ct);
    Task<IVaultSession> OpenAsync(string path, Passphrase password, KeyFile? keyFile, OpenOptions options,
                                  IProgress<VaultProgress>? progress, CancellationToken ct);
    /// Removes orphaned `*~stage-*`, `*.bastion.tmp-*` whose exclusive lock can be taken. Returns bytes reclaimed.
    Task<long> SweepOrphansAsync(IEnumerable<string> directories, CancellationToken ct);
}

public sealed class VaultFactory : IVaultFactory
{
    public VaultFactory(IRandomSource? random = null, IClock? clock = null, IVaultPaths? paths = null,
                        BastionVault.Core.Crypto.IKeyDerivation? kdf = null);
}

// ───────────── seams ─────────────
public interface IRandomSource { void Fill(Span<byte> buffer); }
public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IVaultPaths
{
    string TempFileFor(string vaultPath);                       // "<dir>\<name>.bastion.tmp-<8 hex>"
    string BackupFileFor(string vaultPath);                     // "<dir>\<name>.bastion.bak-<8 hex>"
    string StagingContainerFor(string vaultPath, Guid session); // "<dir>\<name>.bastion~stage-<guid>"
    string FallbackStagingDirectory { get; }                    // %LOCALAPPDATA%\BastionVault\staging
    bool IsUnderCloudSyncRoot(string path);
}
public sealed class SystemRandomSource : IRandomSource { public static readonly SystemRandomSource Instance; }
public sealed class DeterministicRandomSource : IRandomSource { public DeterministicRandomSource(ulong seed); }   // ChaCha/xoshiro based, test only
public sealed class SystemClock : IClock { public static readonly SystemClock Instance; }
public sealed class FixedClock : IClock { public FixedClock(DateTimeOffset now); public DateTimeOffset UtcNow { get; set; } }
public sealed class DefaultVaultPaths : IVaultPaths { public DefaultVaultPaths(IRandomSource random, string? fallbackStagingDirectory = null); }

// ───────────── errors ─────────────
public enum VaultErrorCode
{
    NotAVault, UnsupportedVersion, UnsupportedParameters, HeaderCorrupt, Truncated,
    AuthenticationFailed, IndexCorrupt, IndexInvalid, DataCorrupt,
    ResourceLimit, DiskFull, ReadOnlyTarget, Locked, ChangedOnDisk, SaveVerificationFailed, IoError,
    NameInvalid, NameConflict, InvalidMove, Busy, SessionLocked, ReadOnlySession, Cancelled
}

public class VaultException : Exception
{
    public VaultErrorCode Code { get; }
    public VaultException(VaultErrorCode code, string message, Exception? inner = null);
}
public sealed class VaultFormatException : VaultException { }            // NotAVault, UnsupportedVersion, UnsupportedParameters, HeaderCorrupt, Truncated, IndexCorrupt, IndexInvalid
public sealed class VaultAuthenticationException : VaultException { }    // AuthenticationFailed
public sealed class VaultIntegrityException : VaultException             // DataCorrupt, SaveVerificationFailed
{
    public string? VaultPath { get; }
    public uint? ChunkIndex { get; }
}
public sealed class VaultResourceException : VaultException              // ResourceLimit, DiskFull
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
}
public sealed class VaultIoException : VaultException                    // ReadOnlyTarget, Locked, ChangedOnDisk, IoError
{
    public string? OffendingPath { get; }
}
public sealed class VaultOperationException : VaultException { }         // NameInvalid, NameConflict, InvalidMove, Busy, SessionLocked, ReadOnlySession
```

### Internal contracts (public, "advanced" namespaces; used by tests)

```csharp
namespace BastionVault.Core.Crypto;

public interface IKeyDerivation
{
    /// Argon2id per RFC 9106 (version 0x13). Returns a pinned array the caller must zero. Not interruptible mid-pass;
    /// the token is checked between passes.
    byte[] DeriveArgon2id(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int tagLength, CancellationToken ct);
}
public enum Argon2Type { D = 0, I = 1, Id = 2 }
public sealed class Argon2 : IKeyDerivation
{
    public static readonly Argon2 Instance;
    /// Full RFC 9106 entry point used by the test vectors (secret and associated data are optional).
    public static byte[] Hash(Argon2Type type, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
                              ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
                              uint memoryKiB, uint iterations, uint parallelism, int tagLength, CancellationToken ct);
}
public static class Blake2b
{
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output);          // output 1..64 bytes
    public static void Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output);
}
public sealed class KeyMaterial : IDisposable
{
    public static KeyMaterial Allocate(int length);                                // pinned, zero-initialised
    public static KeyMaterial Random(int length, IRandomSource random);
    public static KeyMaterial From(ReadOnlySpan<byte> bytes);
    public Span<byte> Span { get; }
    public int Length { get; }
    public bool IsDisposed { get; }
    public void Dispose();                                                         // zeroes
}
public static class VaultKeys
{
    public static byte[] ComputeKeyfileDigest(ReadOnlySpan<byte> keyfileBytes);                                  // 32
    public static KeyMaterial DeriveKek(ReadOnlySpan<byte> argon2Output, ReadOnlySpan<byte> keyfileDigestOrEmpty, ReadOnlySpan<byte> kdfSalt);
    public static byte[] DeriveVaultId(ReadOnlySpan<byte> vaultKey);                                              // 16
    public static KeyMaterial DeriveIndexKey(ReadOnlySpan<byte> vaultKey);
    public static KeyMaterial DeriveBlobKey(ReadOnlySpan<byte> vaultKey, ReadOnlySpan<byte> blobId);
}
public static class ChunkCipher
{
    public const int TagSize = 16;
    public static uint ChunkCount(long length, uint chunkSize);                                  // max(1, ceil), checked
    public static long BlobLength(long length, uint chunkSize);
    public static void BuildNonce(uint chunkIndex, Span<byte> nonce12);
    public static void BuildAad(ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId, uint chunkIndex, bool isLast, Span<byte> aad);   // 16+16+16+4+1 = 53 bytes
    public static void EncryptChunk(System.Security.Cryptography.AesGcm aes, ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId,
                                    uint chunkIndex, bool isLast, ReadOnlySpan<byte> plaintext, Span<byte> ciphertextAndTag);
    public static void DecryptChunk(System.Security.Cryptography.AesGcm aes, ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId,
                                    uint chunkIndex, bool isLast, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext);  // throws VaultIntegrityException
}
public static class HeaderCipher
{
    public static void WrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapNonce, ReadOnlySpan<byte> vaultKey, ReadOnlySpan<byte> wrapAad, Span<byte> wrapped48);
    public static KeyMaterial UnwrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapNonce, ReadOnlySpan<byte> wrapped48, ReadOnlySpan<byte> wrapAad); // throws VaultAuthenticationException
    public static byte[] EncryptIndex(ReadOnlySpan<byte> indexKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> paddedPlaintext, ReadOnlySpan<byte> indexAad);
    public static byte[] DecryptIndex(ReadOnlySpan<byte> indexKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> indexAad);  // throws VaultFormatException(IndexCorrupt)
}

namespace BastionVault.Core.Format;

public static class VaultLimits { /* every constant of FORMAT.md §7, plus DefaultChunkSize = 1 MiB */ }
public sealed class VaultHeader
{
    public const int Size = 160;
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x89, 0x42, 0x53, 0x54, 0x4E, 0x0D, 0x0A, 0x1A };
    public ushort FormatVersion { get; init; }      // 1
    public uint Flags { get; init; }
    public KdfParameters Kdf { get; init; }
    public byte[] KdfSalt { get; init; }            // 32
    public byte[] WrapNonce { get; init; }          // 12
    public byte[] WrappedVaultKey { get; init; }    // 48
    public byte[] IndexNonce { get; init; }         // 12
    public byte[] IndexCopyNonce { get; init; }     // 12
    public long IndexLength { get; init; }
    /// FORMAT.md §3.1 steps 1–8 (no resource pre-flight, no KDF).
    public static VaultHeader Parse(ReadOnlySpan<byte> bytes, long fileLength);
    public void Write(Span<byte> destination);      // exactly 160 bytes
    public byte[] BuildWrapAad();
    public byte[] BuildIndexAad();
    public long DataSectionOffset => Size + IndexLength;
}
public sealed class IndexEntry
{
    public EntryKind Kind; public uint Id; public uint ParentId; public string Name = "";
    public long CreatedUtcTicks; public long ModifiedUtcTicks; public uint Attributes; public string Comment = "";
    public byte[]? BlobId; public long DataOffset; public long Length; public uint ChunkSize; public byte[]? BlobHash;
}
public sealed class VaultIndex
{
    public ulong SaveCounter; public long SavedUtcTicks; public long DataSectionLength; public long DataPaddingLength;
    public uint NextEntryId; public List<IndexEntry> Entries = new();
}
public static class IndexSerializer
{
    /// Canonical order (§4.5) + zero padding to PadLadder. Throws VaultFormatException(IndexInvalid) if the tree violates §4.6.
    public static byte[] Serialize(VaultIndex index);
    /// All §4.6 rules; never throws anything but VaultFormatException(IndexInvalid).
    public static VaultIndex Deserialize(ReadOnlySpan<byte> paddedPlaintext);
}
public static class PadLadder
{
    public static long Index(long unpaddedLength);
    public static long Obfuscation(long dataLength);
}
public static class EntryNames
{
    public static NameCheck Validate(string name);                                  // §6.1
    public static string Sanitize(string diskName);                                 // §6.2 steps 1–7
    public static string MakeUnique(string name, Func<string, bool> exists);        // "name (2).ext"
    public static bool Equals(string a, string b);                                  // OrdinalIgnoreCase
    public static readonly StringComparer Comparer;
}
public static class VaultPath
{
    public const char Separator = '\\';
    public static string Format(IEnumerable<string> segments);                      // "\" for none
    public static bool TrySplit(string vaultPath, out string[] segments);           // rejects invalid segments
}
```

### Session internals (not part of the contract, listed for the implementers)

`BastionVault.Core.Session`: `VaultSession : IVaultSession`, `TreeModel` (id → node, children lists, rollups, canonical ordering, undo journal), `StagingStore` (memory buffers + container), `IBlobSource` (Stored@offset / Staged@offset / Memory) with a `BlobReader`, `SaveWriter` (state machine §8.3, modes §8.4), `Importer`, `Exporter`, `Verifier`, `UndoStack`, `KdfPreflight` (memory check).
