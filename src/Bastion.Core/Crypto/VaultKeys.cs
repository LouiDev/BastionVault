using System.Security.Cryptography;

namespace Bastion.Core.Crypto;

/// <summary>
/// The key schedule of FORMAT.md section 2.2 to 2.4: keyfile digest, KEK, vault id, index key and blob keys.
/// </summary>
public static class VaultKeys
{
    /// <summary>Length of the vault key, the KEK, the index key and every blob key in bytes.</summary>
    private const int KeyLength = 32;

    /// <summary>Length of the derived vault id in bytes.</summary>
    private const int VaultIdLength = 16;

    /// <summary>Length of a blob id in bytes.</summary>
    private const int BlobIdLength = 16;

    private static ReadOnlySpan<byte> KeyfileLabel => "bastion/v1/keyfile"u8;

    private static ReadOnlySpan<byte> KekLabel => "bastion/v1/kek"u8;

    private static ReadOnlySpan<byte> VaultIdLabel => "bastion/v1/vaultid"u8;

    private static ReadOnlySpan<byte> IndexLabel => "bastion/v1/index"u8;

    private static ReadOnlySpan<byte> BlobLabel => "bastion/v1/blob"u8;

    /// <summary>Computes <c>HMAC-SHA256("bastion/v1/keyfile", keyfileBytes)</c>.</summary>
    /// <param name="keyfileBytes">Raw keyfile content (1 byte .. 1 MiB).</param>
    /// <returns>The 32-byte keyfile digest.</returns>
    public static byte[] ComputeKeyfileDigest(ReadOnlySpan<byte> keyfileBytes)
    {
        byte[] digest = GC.AllocateArray<byte>(KeyLength, pinned: true);
        HMACSHA256.HashData(KeyfileLabel, keyfileBytes, digest);
        return digest;
    }

    /// <summary>
    /// Derives the key-encryption key:
    /// <c>HKDF-SHA256(ikm = argon2Output || keyfileDigest, salt = kdfSalt, info = "bastion/v1/kek" || u8 keyfilePresent, L = 32)</c>.
    /// </summary>
    /// <param name="argon2Output">The 32-byte Argon2id output.</param>
    /// <param name="keyfileDigestOrEmpty">The 32-byte keyfile digest, or an empty span when no keyfile is used.</param>
    /// <param name="kdfSalt">The 32-byte KDF salt from the header.</param>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    public static KeyMaterial DeriveKek(ReadOnlySpan<byte> argon2Output, ReadOnlySpan<byte> keyfileDigestOrEmpty, ReadOnlySpan<byte> kdfSalt)
    {
        if (argon2Output.IsEmpty)
        {
            throw new ArgumentException("The Argon2id output must not be empty.", nameof(argon2Output));
        }

        if (!keyfileDigestOrEmpty.IsEmpty && keyfileDigestOrEmpty.Length != KeyLength)
        {
            throw new ArgumentException($"The keyfile digest must be empty or {KeyLength} bytes (was {keyfileDigestOrEmpty.Length}).", nameof(keyfileDigestOrEmpty));
        }

        if (kdfSalt.IsEmpty)
        {
            throw new ArgumentException("The KDF salt must not be empty.", nameof(kdfSalt));
        }

        bool keyfilePresent = !keyfileDigestOrEmpty.IsEmpty;

        Span<byte> info = stackalloc byte[KekLabel.Length + 1];
        KekLabel.CopyTo(info);
        info[KekLabel.Length] = keyfilePresent ? (byte)1 : (byte)0;

        using KeyMaterial ikm = KeyMaterial.Allocate(argon2Output.Length + keyfileDigestOrEmpty.Length);
        argon2Output.CopyTo(ikm.Span);
        keyfileDigestOrEmpty.CopyTo(ikm.Span[argon2Output.Length..]);

        KeyMaterial kek = KeyMaterial.Allocate(KeyLength);
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm.Span, kek.Span, kdfSalt, info);
        }
        catch
        {
            kek.Dispose();
            throw;
        }

        return kek;
    }

    /// <summary>Derives the 16-byte vault id: <c>HKDF-Expand(vaultKey, "bastion/v1/vaultid", 16)</c>. Never stored.</summary>
    /// <param name="vaultKey">The 32-byte vault key.</param>
    /// <exception cref="ArgumentException"><paramref name="vaultKey"/> is not 32 bytes.</exception>
    public static byte[] DeriveVaultId(ReadOnlySpan<byte> vaultKey)
    {
        RequireVaultKey(vaultKey);
        byte[] vaultId = new byte[VaultIdLength];
        HKDF.Expand(HashAlgorithmName.SHA256, vaultKey, vaultId, VaultIdLabel);
        return vaultId;
    }

    /// <summary>Derives the index key: <c>HKDF-Expand(vaultKey, "bastion/v1/index", 32)</c>.</summary>
    /// <param name="vaultKey">The 32-byte vault key.</param>
    /// <exception cref="ArgumentException"><paramref name="vaultKey"/> is not 32 bytes.</exception>
    public static KeyMaterial DeriveIndexKey(ReadOnlySpan<byte> vaultKey)
    {
        RequireVaultKey(vaultKey);
        KeyMaterial key = KeyMaterial.Allocate(KeyLength);
        try
        {
            HKDF.Expand(HashAlgorithmName.SHA256, vaultKey, key.Span, IndexLabel);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return key;
    }

    /// <summary>Derives a per-blob key: <c>HKDF-Expand(vaultKey, "bastion/v1/blob" || blobId, 32)</c>.</summary>
    /// <param name="vaultKey">The 32-byte vault key.</param>
    /// <param name="blobId">The 16-byte blob id.</param>
    /// <exception cref="ArgumentException"><paramref name="vaultKey"/> is not 32 bytes or <paramref name="blobId"/> is not 16 bytes.</exception>
    public static KeyMaterial DeriveBlobKey(ReadOnlySpan<byte> vaultKey, ReadOnlySpan<byte> blobId)
    {
        RequireVaultKey(vaultKey);
        if (blobId.Length != BlobIdLength)
        {
            throw new ArgumentException($"A blob id must be {BlobIdLength} bytes (was {blobId.Length}).", nameof(blobId));
        }

        Span<byte> info = stackalloc byte[BlobLabel.Length + BlobIdLength];
        BlobLabel.CopyTo(info);
        blobId.CopyTo(info[BlobLabel.Length..]);

        KeyMaterial key = KeyMaterial.Allocate(KeyLength);
        try
        {
            HKDF.Expand(HashAlgorithmName.SHA256, vaultKey, key.Span, info);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return key;
    }

    private static void RequireVaultKey(ReadOnlySpan<byte> vaultKey)
    {
        if (vaultKey.Length != KeyLength)
        {
            throw new ArgumentException($"The vault key must be {KeyLength} bytes (was {vaultKey.Length}).", nameof(vaultKey));
        }
    }
}
