namespace Bastion.Core.Session;

/// <summary>
/// Turns file-system failures into <see cref="VaultException"/>s. API.md rule 5: no raw
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> ever leaves Core.
/// </summary>
internal static class IoGuard
{
    private const int ErrorWriteProtect = 0x13;
    private const int ErrorNotReady = 0x15;
    private const int ErrorSharingViolation = 0x20;
    private const int ErrorLockViolation = 0x21;
    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;
    private const int ErrorNotSameDevice = 0x11;
    private const int ErrorInvalidParameter = 0x57;

    /// <summary>Win32 codes a File.Replace may report while another process still holds the file.</summary>
    private static readonly int[] TransientReplaceCodes =
    [
        ErrorSharingViolation,
        ErrorLockViolation,
        0x497, // ERROR_UNABLE_TO_REMOVE_REPLACED
        0x498, // ERROR_UNABLE_TO_MOVE_REPLACEMENT
        0x499, // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2
    ];

    /// <summary>Maps an I/O failure to the vault error it represents.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <param name="path">The path the operation was working on, when known.</param>
    /// <returns>The exception to throw instead, or the original when it already is a vault error.</returns>
    public static Exception Translate(Exception exception, string? path)
    {
        switch (exception)
        {
            case VaultException:
            case OperationCanceledException:
                return exception;

            case ObjectDisposedException:
                // The only disposable state an operation shares with the outside world is the key
                // material, and Lock zeroes it from any thread at any time.
                return new VaultOperationException(
                    VaultErrorCode.SessionLocked,
                    "The session was locked while this operation was running.",
                    exception);

            case UnauthorizedAccessException:
                return new VaultIoException(
                    HasReadOnlyAttribute(path) ? VaultErrorCode.ReadOnlyTarget : VaultErrorCode.IoError,
                    Describe(exception, path),
                    exception)
                { OffendingPath = path };

            case IOException io:
                return new VaultIoException(CodeFor(io), Describe(exception, path), exception) { OffendingPath = path };

            default:
                return exception;
        }
    }

    /// <summary>Classifies an <see cref="IOException"/> by its Win32 code.</summary>
    /// <param name="exception">The caught exception.</param>
    public static VaultErrorCode CodeFor(IOException exception)
    {
        return Win32Of(exception) switch
        {
            ErrorDiskFull or ErrorHandleDiskFull => VaultErrorCode.DiskFull,
            ErrorSharingViolation or ErrorLockViolation => VaultErrorCode.Locked,
            ErrorWriteProtect or ErrorNotReady => VaultErrorCode.ReadOnlyTarget,
            _ => VaultErrorCode.IoError,
        };
    }

    /// <summary>True when a failed File.Replace is worth retrying.</summary>
    /// <param name="exception">The caught exception.</param>
    public static bool IsTransientReplaceFailure(IOException exception) =>
        Array.IndexOf(TransientReplaceCodes, Win32Of(exception)) >= 0;

    /// <summary>True when File.Replace cannot work here and the two-move fallback must be used.</summary>
    /// <param name="exception">The caught exception.</param>
    public static bool IsReplaceUnsupported(IOException exception) =>
        Win32Of(exception) is ErrorNotSameDevice or ErrorInvalidParameter;

    /// <summary>Runs a synchronous file operation and translates its failures.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="operation">The operation.</param>
    /// <param name="path">Path for the error message.</param>
    public static T Run<T>(Func<T> operation, string? path)
    {
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    /// <summary>Runs a synchronous file operation and translates its failures.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="path">Path for the error message.</param>
    public static void Run(Action operation, string? path)
    {
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            throw Translate(ex, path);
        }
    }

    /// <summary>The facility mask of an HRESULT; an <see cref="int"/>, never a <see cref="uint"/>.</summary>
    private const int FacilityMask = unchecked((int)0xFFFF0000);

    /// <summary>The FACILITY_WIN32 severity-and-facility bits.</summary>
    private const int FacilityWin32 = unchecked((int)0x80070000);

    /// <summary>The Win32 status embedded in an exception HRESULT, or 0.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <remarks>
    /// Both constants are <see cref="int"/> on purpose: with an unsigned mask the whole comparison is
    /// promoted to <see cref="long"/>, the sign-extended HRESULT never matches, and every classification
    /// below silently collapses to <see cref="VaultErrorCode.IoError"/> while the replace loop never
    /// retries.
    /// </remarks>
    private static int Win32Of(Exception exception) =>
        (exception.HResult & FacilityMask) == FacilityWin32 ? exception.HResult & 0xFFFF : 0;

    /// <summary>True when the path exists and carries the read-only attribute.</summary>
    /// <param name="path">Path to test.</param>
    private static bool HasReadOnlyAttribute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Builds a message that names the path but never the vault contents.</summary>
    /// <param name="exception">The caught exception.</param>
    /// <param name="path">Path involved, when known.</param>
    private static string Describe(Exception exception, string? path) =>
        string.IsNullOrEmpty(path)
            ? exception.Message
            : $"{exception.Message} (path: {path})";
}
