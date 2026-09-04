namespace Bastion.Core.Crypto;

/// <summary>The password key-derivation seam (FORMAT.md section 2.3).</summary>
public interface IKeyDerivation
{
    /// <summary>
    /// Runs Argon2id per RFC 9106 (version 0x13). Not interruptible mid-pass; the token is checked
    /// between passes.
    /// </summary>
    /// <param name="password">UTF-8 password bytes.</param>
    /// <param name="salt">The 32-byte KDF salt from the header.</param>
    /// <param name="parameters">Memory, iteration and parallelism cost.</param>
    /// <param name="tagLength">Output length in bytes; 32 for the vault KEK.</param>
    /// <param name="ct">Cancellation token, checked between passes.</param>
    /// <returns>A pinned array that the caller must zero when done.</returns>
    byte[] DeriveArgon2id(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int tagLength, CancellationToken ct);
}
