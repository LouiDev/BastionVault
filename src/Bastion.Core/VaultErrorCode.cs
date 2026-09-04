namespace Bastion.Core;

/// <summary>
/// Every failure Core reports (FORMAT.md section 9). Each one arrives as a <see cref="VaultException"/>
/// subclass carrying this code; no raw <see cref="IOException"/> or cryptography exception leaves Core.
/// </summary>
public enum VaultErrorCode
{
    /// <summary>The magic bytes do not match; the file is not a vault.</summary>
    NotAVault,

    /// <summary>The format version is newer than this build understands.</summary>
    UnsupportedVersion,

    /// <summary>A header field, KDF parameter, cipher id or keyfile length is outside the supported set.</summary>
    UnsupportedParameters,

    /// <summary>A structural header field is impossible (header length, reserved bytes, index length, equal nonces).</summary>
    HeaderCorrupt,

    /// <summary>The file is shorter than the length equation of FORMAT.md section 1 requires.</summary>
    Truncated,

    /// <summary>Wrong password, wrong or missing keyfile, or an altered header. One bucket by design.</summary>
    AuthenticationFailed,

    /// <summary>Both the index and its copy failed to authenticate.</summary>
    IndexCorrupt,

    /// <summary>The decrypted index violates a validity rule of FORMAT.md section 4.6.</summary>
    IndexInvalid,

    /// <summary>A chunk failed authentication or a blob hash did not match. Carries the entry path and chunk index.</summary>
    DataCorrupt,

    /// <summary>The operation would need more memory than the machine can safely provide (KDF pre-flight).</summary>
    ResourceLimit,

    /// <summary>Not enough free space on the target volume.</summary>
    DiskFull,

    /// <summary>The vault file carries the read-only attribute.</summary>
    ReadOnlyTarget,

    /// <summary>A sharing violation that persisted through the retries. Carries the path.</summary>
    Locked,

    /// <summary>The vault file changed on disk since it was opened or last saved.</summary>
    ChangedOnDisk,

    /// <summary>The post-save verification failed; the backup was kept.</summary>
    SaveVerificationFailed,

    /// <summary>Any other I/O failure, wrapped.</summary>
    IoError,

    /// <summary>A name violates FORMAT.md section 6.1.</summary>
    NameInvalid,

    /// <summary>A sibling already carries that name (OrdinalIgnoreCase).</summary>
    NameConflict,

    /// <summary>A move into the entry itself, into one of its descendants, or of the root.</summary>
    InvalidMove,

    /// <summary>Another operation already holds the session lock; calls are never queued.</summary>
    Busy,

    /// <summary>The session is locked; unlock it before running the operation.</summary>
    SessionLocked,

    /// <summary>The session was opened read-only.</summary>
    ReadOnlySession,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,
}
