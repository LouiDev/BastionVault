using System.Security.Cryptography;

namespace Bastion.Core.Crypto;

/// <summary>
/// AES-256-GCM for the two header-adjacent secrets: the wrapped vault key (FORMAT.md section 2.5)
/// and the encrypted index (section 4.1).
/// </summary>
public static class HeaderCipher
{
    /// <summary>Length of a GCM tag in bytes.</summary>
    private const int TagSize = 16;

    /// <summary>Length of a GCM nonce in bytes.</summary>
    private const int NonceSize = 12;

    /// <summary>Length of the vault key and of the key-encryption key in bytes.</summary>
    private const int KeySize = 32;

    /// <summary>Length of the wrapped vault key in bytes: 32 ciphertext plus a 16-byte tag.</summary>
    private const int WrappedSize = KeySize + TagSize;

    /// <summary>The message FORMAT.md section 2.5 prescribes for a failed unwrap.</summary>
    private const string UnwrapFailureMessage = "wrong password or keyfile, or the vault header has been altered";

    /// <summary>Wraps the vault key: 32 bytes of ciphertext followed by a 16-byte tag.</summary>
    /// <param name="kek">The 32-byte key-encryption key.</param>
    /// <param name="wrapNonce">The 12-byte wrap nonce; fresh for every wrap.</param>
    /// <param name="vaultKey">The 32-byte vault key to protect.</param>
    /// <param name="wrapAad">The wrap AAD from <see cref="Bastion.Core.Format.VaultHeader.BuildWrapAad"/>.</param>
    /// <param name="wrapped48">Destination, exactly 48 bytes.</param>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    public static void WrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapNonce, ReadOnlySpan<byte> vaultKey,
                                    ReadOnlySpan<byte> wrapAad, Span<byte> wrapped48)
    {
        RequireLength(kek, KeySize, nameof(kek));
        RequireLength(wrapNonce, NonceSize, nameof(wrapNonce));
        RequireLength(vaultKey, KeySize, nameof(vaultKey));
        RequireLength(wrapped48, WrappedSize, nameof(wrapped48));

        using AesGcm aes = new(kek, TagSize);
        aes.Encrypt(wrapNonce, vaultKey, wrapped48[..KeySize], wrapped48[KeySize..], wrapAad);
    }

    /// <summary>Unwraps the vault key.</summary>
    /// <param name="kek">The 32-byte key-encryption key.</param>
    /// <param name="wrapNonce">The 12-byte wrap nonce from the header.</param>
    /// <param name="wrapped48">The 48-byte wrapped vault key from the header.</param>
    /// <param name="wrapAad">The wrap AAD from <see cref="Bastion.Core.Format.VaultHeader.BuildWrapAad"/>.</param>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    /// <exception cref="VaultAuthenticationException"><see cref="VaultErrorCode.AuthenticationFailed"/> when the tag does not authenticate.</exception>
    public static KeyMaterial UnwrapVaultKey(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapNonce, ReadOnlySpan<byte> wrapped48,
                                             ReadOnlySpan<byte> wrapAad)
    {
        RequireLength(kek, KeySize, nameof(kek));
        RequireLength(wrapNonce, NonceSize, nameof(wrapNonce));
        RequireLength(wrapped48, WrappedSize, nameof(wrapped48));

        KeyMaterial vaultKey = KeyMaterial.Allocate(KeySize);
        try
        {
            using AesGcm aes = new(kek, TagSize);
            aes.Decrypt(wrapNonce, wrapped48[..KeySize], wrapped48[KeySize..], vaultKey.Span, wrapAad);
        }
        catch (CryptographicException ex)
        {
            vaultKey.Dispose();
            throw new VaultAuthenticationException(VaultErrorCode.AuthenticationFailed, UnwrapFailureMessage, ex);
        }
        catch
        {
            vaultKey.Dispose();
            throw;
        }

        return vaultKey;
    }

    /// <summary>Encrypts the padded index plaintext.</summary>
    /// <param name="indexKey">The 32-byte index key.</param>
    /// <param name="nonce">The 12-byte index nonce or index-copy nonce.</param>
    /// <param name="paddedPlaintext">Serialized index padded to the ladder of FORMAT.md section 4.2.</param>
    /// <param name="indexAad">The index AAD from <see cref="Bastion.Core.Format.VaultHeader.BuildIndexAad"/>.</param>
    /// <returns>Ciphertext followed by the 16-byte tag.</returns>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    public static byte[] EncryptIndex(ReadOnlySpan<byte> indexKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> paddedPlaintext,
                                      ReadOnlySpan<byte> indexAad)
    {
        RequireLength(indexKey, KeySize, nameof(indexKey));
        RequireLength(nonce, NonceSize, nameof(nonce));

        byte[] ciphertext = new byte[paddedPlaintext.Length + TagSize];
        using AesGcm aes = new(indexKey, TagSize);
        aes.Encrypt(nonce, paddedPlaintext, ciphertext.AsSpan(0, paddedPlaintext.Length), ciphertext.AsSpan(paddedPlaintext.Length), indexAad);
        return ciphertext;
    }

    /// <summary>Authenticates and decrypts an index (or its copy).</summary>
    /// <param name="indexKey">The 32-byte index key.</param>
    /// <param name="nonce">The 12-byte nonce that produced this ciphertext.</param>
    /// <param name="ciphertext">Ciphertext followed by its 16-byte tag.</param>
    /// <param name="indexAad">The index AAD from <see cref="Bastion.Core.Format.VaultHeader.BuildIndexAad"/>.</param>
    /// <returns>The padded index plaintext.</returns>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.IndexCorrupt"/> when the tag does not authenticate.</exception>
    public static byte[] DecryptIndex(ReadOnlySpan<byte> indexKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext,
                                      ReadOnlySpan<byte> indexAad)
    {
        RequireLength(indexKey, KeySize, nameof(indexKey));
        RequireLength(nonce, NonceSize, nameof(nonce));

        if (ciphertext.Length < TagSize)
        {
            throw new ArgumentException($"An encrypted index carries at least a {TagSize}-byte tag (was {ciphertext.Length}).", nameof(ciphertext));
        }

        byte[] plaintext = new byte[ciphertext.Length - TagSize];
        try
        {
            using AesGcm aes = new(indexKey, TagSize);
            aes.Decrypt(nonce, ciphertext[..plaintext.Length], ciphertext[plaintext.Length..], plaintext, indexAad);
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new VaultFormatException(
                VaultErrorCode.IndexCorrupt,
                "the vault index failed authentication: it has been altered or damaged",
                ex);
        }

        return plaintext;
    }

    private static void RequireLength(ReadOnlySpan<byte> value, int expected, string parameterName)
    {
        if (value.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} bytes (was {value.Length}).", parameterName);
        }
    }

    private static void RequireLength(Span<byte> value, int expected, string parameterName) =>
        RequireLength((ReadOnlySpan<byte>)value, expected, parameterName);
}
