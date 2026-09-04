namespace BastionVault.Core;

/// <summary>
/// Creates and opens vault files, and cleans up orphaned temporary artefacts.
/// </summary>
public interface IVaultFactory
{
    /// <summary>
    /// Reads and validates the 160-byte header only (FORMAT.md section 3.1 steps 1-8). No key derivation runs,
    /// so this is cheap and needs no credentials.
    /// </summary>
    /// <param name="path">Path of the vault file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="VaultFormatException">The file is not a vault, or its header is unsupported or corrupt.</exception>
    Task<VaultHeaderInfo> ReadHeaderAsync(string path, CancellationToken ct);

    /// <summary>Creates a new, empty vault at <paramref name="path"/> and returns an open session for it.</summary>
    /// <param name="path">Path of the new vault file.</param>
    /// <param name="password">Password protecting the vault.</param>
    /// <param name="keyFile">Optional keyfile used as a second factor.</param>
    /// <param name="kdf">Argon2id parameters to store in the header.</param>
    /// <param name="progress">Optional progress sink; the KDF phase reports <c>IsCancellable = false</c>.</param>
    /// <param name="ct">Cancellation token; a cancelled create leaves no file behind.</param>
    Task<IVaultSession> CreateAsync(string path, Passphrase password, KeyFile? keyFile, KdfParameters kdf,
                                    IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>Opens an existing vault: header checks, key derivation, unwrap, index decryption and validation.</summary>
    /// <param name="path">Path of the vault file.</param>
    /// <param name="password">The vault password.</param>
    /// <param name="keyFile">The keyfile, when one is used.</param>
    /// <param name="options">Read-only mode and staging placement.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token; a cancelled open leaves nothing behind.</param>
    /// <exception cref="VaultAuthenticationException"><see cref="VaultErrorCode.AuthenticationFailed"/> for a wrong password or keyfile, or an altered header.</exception>
    Task<IVaultSession> OpenAsync(string path, Passphrase password, KeyFile? keyFile, OpenOptions options,
                                  IProgress<VaultProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Removes orphaned <c>*~stage-*</c> and <c>*.bastion.tmp-*</c> files whose exclusive lock can be taken
    /// (a live session holds its own container open).
    /// </summary>
    /// <param name="directories">Directories to sweep.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of bytes reclaimed.</returns>
    Task<long> SweepOrphansAsync(IEnumerable<string> directories, CancellationToken ct);
}
