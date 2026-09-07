using System.Security.Cryptography;
using BastionVault.Core.Crypto;

namespace BastionVault.Core.Session;

/// <summary>
/// A password, keyfile or KDF change that has been derived but not written yet. It stays pending until
/// the next save, which is the single commit point (FORMAT.md section 8.4).
/// </summary>
internal sealed class PendingCredentials : IDisposable
{
    /// <summary>Argon2id parameters to store in the new header.</summary>
    public required KdfParameters Kdf { get; init; }

    /// <summary>The fresh 32-byte salt the KEK was derived with.</summary>
    public required byte[] KdfSalt { get; init; }

    /// <summary>The derived key-encryption key.</summary>
    public required KeyMaterial Kek { get; init; }

    /// <summary>Whether the save re-keys the vault or only rewraps the existing vault key.</summary>
    public required CredentialChangeMode Mode { get; init; }

    /// <summary>The vault key the save will install, for <see cref="CredentialChangeMode.Rekey"/>.</summary>
    public KeyMaterial? NewVaultKey { get; init; }

    /// <inheritdoc />
    public void Dispose()
    {
        Kek.Dispose();
        NewVaultKey?.Dispose();
        CryptographicOperations.ZeroMemory(KdfSalt);
    }
}

/// <summary>Runs the password KDF and turns a password plus an optional keyfile into a KEK.</summary>
internal static class Credentials
{
    /// <summary>Length of the Argon2id tag and of every derived key.</summary>
    private const int KeyLength = 32;

    /// <summary>
    /// Runs Argon2id on the thread pool and derives the KEK (FORMAT.md section 2.3). The KDF phase itself
    /// is not interruptible; the token is honoured between passes and again right after it returns.
    /// </summary>
    /// <param name="kdf">Key-derivation seam.</param>
    /// <param name="password">The password.</param>
    /// <param name="keyFile">The keyfile, or <see langword="null"/>.</param>
    /// <param name="salt">The 32-byte KDF salt.</param>
    /// <param name="parameters">Argon2id cost parameters.</param>
    /// <param name="progress">Progress sink; the KDF phase reports that it cannot be cancelled.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<KeyMaterial> DeriveKekAsync(
        IKeyDerivation kdf,
        Passphrase password,
        KeyFile? keyFile,
        byte[] salt,
        KdfParameters parameters,
        ProgressThrottle? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kdf);
        ArgumentNullException.ThrowIfNull(password);

        ct.ThrowIfCancellationRequested();
        progress?.Start(null, isCancellable: false);

        // The span of a Passphrase cannot cross an await, so the bytes move into a pinned buffer first.
        using KeyMaterial secret = KeyMaterial.From(password.Bytes);
        byte[]? argon2 = null;
        try
        {
            try
            {
                argon2 = await Task.Run(
                    () => kdf.DeriveArgon2id(secret.Span, salt, parameters, KeyLength, ct),
                    ct).ConfigureAwait(false);
            }
            catch (OutOfMemoryException oom)
            {
                throw TranslateOutOfMemory(parameters, oom);
            }

            ct.ThrowIfCancellationRequested();
            return keyFile is null
                ? VaultKeys.DeriveKek(argon2, ReadOnlySpan<byte>.Empty, salt)
                : VaultKeys.DeriveKek(argon2, keyFile.Digest, salt);
        }
        finally
        {
            if (argon2 is not null)
            {
                CryptographicOperations.ZeroMemory(argon2);
            }
        }
    }

    /// <summary>
    /// FORMAT.md section 3.1 step 9: refuse a KDF that would claim more than 75 % of the memory the
    /// machine physically has. The question itself lives in <see cref="KdfPreflight"/> so a UI can ask
    /// it before the button is pressed; this is the refusal.
    /// </summary>
    /// <param name="parameters">Argon2id parameters from the header.</param>
    /// <exception cref="VaultResourceException"><see cref="VaultErrorCode.ResourceLimit"/>.</exception>
    public static void PreflightMemory(KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        KdfPreflightResult verdict = KdfPreflight.Check(parameters);
        if (verdict.Fits)
        {
            return;
        }

        throw new VaultResourceException(
            VaultErrorCode.ResourceLimit,
            $"Opening this vault needs {Mebibytes(verdict.RequiredBytes)} MiB of memory for the key derivation; " +
            $"this machine has {Mebibytes(verdict.InstalledBytes)} MiB installed.")
        {
            RequiredBytes = verdict.RequiredBytes,
            AvailableBytes = verdict.BudgetBytes,
        };
    }

    /// <summary>
    /// The one place an <see cref="OutOfMemoryException"/> from the KDF is turned into a vault error
    /// (API.md rule 5). The pinned Argon2 block array is the only allocation in Core large enough to fail
    /// on a machine that passed the pre-flight; by the time this runs, that allocation has been released
    /// and the runtime has already produced the exception object itself, so building a small wrapper here
    /// is not the hazard that wrapping an arbitrary OOM would be.
    /// </summary>
    /// <param name="parameters">The parameters whose derivation failed.</param>
    /// <param name="inner">The allocation failure.</param>
    private static VaultResourceException TranslateOutOfMemory(KdfParameters parameters, OutOfMemoryException inner)
    {
        long required = parameters.MemoryBytes;
        long available = KdfPreflight.AvailablePhysicalMemoryBytes();
        string now = available > 0 ? $"{Mebibytes(available)} MiB is free right now" : "less than that is free right now";

        return new VaultResourceException(
            VaultErrorCode.ResourceLimit,
            $"The key derivation needs {Mebibytes(required)} MiB of memory and {now}. " +
            "Close other programs and try again.",
            inner)
        {
            RequiredBytes = required,
            AvailableBytes = available,
        };
    }

    /// <summary>Rounds a byte count up to whole mebibytes, for the message.</summary>
    /// <param name="bytes">Byte count to convert.</param>
    private static long Mebibytes(long bytes) => (bytes + (1024 * 1024) - 1) / (1024 * 1024);
}
