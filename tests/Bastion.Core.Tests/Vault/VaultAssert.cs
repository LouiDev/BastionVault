namespace Bastion.Core.Tests.Vault;

/// <summary>
/// Assertions about the exception contract of API.md: every failure is a <see cref="VaultException"/>
/// of the subtype that owns its <see cref="VaultErrorCode"/>, never a bare <see cref="IOException"/> or
/// <see cref="System.Security.Cryptography.CryptographicException"/>.
/// </summary>
internal static class VaultAssert
{
    /// <summary>Asserts the code and the subtype the API contract pairs with it.</summary>
    /// <param name="exception">The exception a vault operation threw.</param>
    /// <param name="expected">The code FORMAT.md demands.</param>
    /// <param name="because">Description of the case, shown when the assertion fails.</param>
    public static void Failure(VaultException exception, VaultErrorCode expected, string because)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Assert.True(
            exception.Code == expected,
            $"{because}: expected {expected} but got {exception.Code} ({exception.GetType().Name}: {exception.Message}).");

        Type subtype = SubtypeFor(expected);
        Assert.True(
            subtype.IsInstanceOfType(exception),
            $"{because}: {expected} must be reported as {subtype.Name}, not {exception.GetType().Name}.");

        Assert.False(string.IsNullOrWhiteSpace(exception.Message), $"{because}: the exception carries no message.");
    }

    /// <summary>The <see cref="VaultException"/> subtype that owns an error code, per API.md.</summary>
    /// <param name="code">The error code.</param>
    public static Type SubtypeFor(VaultErrorCode code) => code switch
    {
        VaultErrorCode.NotAVault or
        VaultErrorCode.UnsupportedVersion or
        VaultErrorCode.UnsupportedParameters or
        VaultErrorCode.HeaderCorrupt or
        VaultErrorCode.Truncated or
        VaultErrorCode.IndexCorrupt or
        VaultErrorCode.IndexInvalid => typeof(VaultFormatException),

        VaultErrorCode.AuthenticationFailed => typeof(VaultAuthenticationException),

        VaultErrorCode.DataCorrupt or
        VaultErrorCode.SaveVerificationFailed => typeof(VaultIntegrityException),

        VaultErrorCode.ResourceLimit or
        VaultErrorCode.DiskFull => typeof(VaultResourceException),

        VaultErrorCode.ReadOnlyTarget or
        VaultErrorCode.Locked or
        VaultErrorCode.ChangedOnDisk or
        VaultErrorCode.IoError => typeof(VaultIoException),

        _ => typeof(VaultOperationException),
    };
}
