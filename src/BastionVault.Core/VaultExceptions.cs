namespace BastionVault.Core;

/// <summary>
/// Base of every error Core reports. The only other exception types that may escape Core are
/// <see cref="OperationCanceledException"/> and the argument family for caller misuse.
/// </summary>
public class VaultException : Exception
{
    /// <summary>Creates a vault exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultException(VaultErrorCode code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>The error code that classifies this failure.</summary>
    public VaultErrorCode Code { get; }
}

/// <summary>
/// The file is not a readable v1 vault: <see cref="VaultErrorCode.NotAVault"/>,
/// <see cref="VaultErrorCode.UnsupportedVersion"/>, <see cref="VaultErrorCode.UnsupportedParameters"/>,
/// <see cref="VaultErrorCode.HeaderCorrupt"/>, <see cref="VaultErrorCode.Truncated"/>,
/// <see cref="VaultErrorCode.IndexCorrupt"/> or <see cref="VaultErrorCode.IndexInvalid"/>.
/// </summary>
public sealed class VaultFormatException : VaultException
{
    /// <summary>Creates a format exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultFormatException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }
}

/// <summary>
/// <see cref="VaultErrorCode.AuthenticationFailed"/>: wrong password, wrong or missing keyfile, or an
/// altered header. These cases are deliberately indistinguishable.
/// </summary>
public sealed class VaultAuthenticationException : VaultException
{
    /// <summary>Creates an authentication exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultAuthenticationException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }
}

/// <summary>
/// Authenticated data did not match: <see cref="VaultErrorCode.DataCorrupt"/> or
/// <see cref="VaultErrorCode.SaveVerificationFailed"/>.
/// </summary>
public sealed class VaultIntegrityException : VaultException
{
    /// <summary>Creates an integrity exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultIntegrityException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }

    /// <summary>In-vault path of the affected entry, when the failure can be attributed to one.</summary>
    public string? VaultPath { get; init; }

    /// <summary>Index of the chunk that failed, when the failure can be attributed to one.</summary>
    public uint? ChunkIndex { get; init; }
}

/// <summary>
/// The machine cannot satisfy the request: <see cref="VaultErrorCode.ResourceLimit"/> or
/// <see cref="VaultErrorCode.DiskFull"/>.
/// </summary>
public sealed class VaultResourceException : VaultException
{
    /// <summary>Creates a resource exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultResourceException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }

    /// <summary>Bytes the operation needs.</summary>
    public long RequiredBytes { get; init; }

    /// <summary>Bytes actually available.</summary>
    public long AvailableBytes { get; init; }
}

/// <summary>
/// A file-system failure, wrapped: <see cref="VaultErrorCode.ReadOnlyTarget"/>,
/// <see cref="VaultErrorCode.Locked"/>, <see cref="VaultErrorCode.ChangedOnDisk"/> or
/// <see cref="VaultErrorCode.IoError"/>.
/// </summary>
public sealed class VaultIoException : VaultException
{
    /// <summary>Creates an I/O exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultIoException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }

    /// <summary>The path the failure refers to, when one is known.</summary>
    public string? OffendingPath { get; init; }
}

/// <summary>
/// The request itself is not allowed right now: <see cref="VaultErrorCode.NameInvalid"/>,
/// <see cref="VaultErrorCode.NameConflict"/>, <see cref="VaultErrorCode.InvalidMove"/>,
/// <see cref="VaultErrorCode.Busy"/>, <see cref="VaultErrorCode.SessionLocked"/> or
/// <see cref="VaultErrorCode.ReadOnlySession"/>.
/// </summary>
public sealed class VaultOperationException : VaultException
{
    /// <summary>Creates an operation exception.</summary>
    /// <param name="code">The error code that classifies the failure.</param>
    /// <param name="message">Human-readable, user-facing description.</param>
    /// <param name="inner">The wrapped underlying exception, if any.</param>
    public VaultOperationException(VaultErrorCode code, string message, Exception? inner = null)
        : base(code, message, inner)
    {
    }
}
