namespace Bastion.Core;

/// <summary>
/// What a vault file discloses before any key derivation happens
/// (<see cref="IVaultFactory.ReadHeaderAsync"/>, FORMAT.md §3).
/// </summary>
/// <param name="FormatVersion">Format version stored in the header; 1 for v1 vaults.</param>
/// <param name="Kdf">Argon2id cost parameters stored in the header.</param>
/// <param name="FileLength">Total length of the vault file in bytes.</param>
/// <param name="IndexLength">Encrypted index length in bytes (ciphertext including the 16-byte tag).</param>
/// <param name="RequiredMemoryBytes">Physical memory the KDF will need (<see cref="KdfParameters.MemoryBytes"/>).</param>
public sealed record VaultHeaderInfo(
    ushort FormatVersion,
    KdfParameters Kdf,
    long FileLength,
    long IndexLength,
    long RequiredMemoryBytes);
