using System.Buffers.Binary;
using System.Security.Cryptography;
using BastionVault.Core.Format;

namespace BastionVault.Core.Crypto;

/// <summary>
/// AES-256-GCM chunk framing for blob content (FORMAT.md section 2.7). The reader derives chunk
/// count, chunk lengths and the last-chunk flag from the index; it never trusts framing in the data section.
/// </summary>
public static class ChunkCipher
{
    /// <summary>Length of a GCM tag in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Length of the nonce of a chunk in bytes.</summary>
    private const int NonceSize = 12;

    /// <summary>Length of the AAD of a chunk in bytes: 16 + 16 + 16 + 4 + 1.</summary>
    private const int AadSize = 53;

    /// <summary>Length of a vault id in bytes.</summary>
    private const int VaultIdSize = 16;

    /// <summary>Length of a blob id in bytes.</summary>
    private const int BlobIdSize = 16;

    private static ReadOnlySpan<byte> ChunkLabel => "bastion/v1/chunk"u8;

    /// <summary>Number of chunks of a blob: <c>max(1, ceil(length / chunkSize))</c>, in checked arithmetic.</summary>
    /// <param name="length">Plaintext length in bytes.</param>
    /// <param name="chunkSize">Chunk size in bytes (a power of two in 64 KiB .. 64 MiB).</param>
    /// <exception cref="VaultFormatException">
    /// <see cref="VaultErrorCode.IndexInvalid"/> - the chunk size is not a power of two in range, the length is
    /// negative, or the blob would need more than <c>2^32 - 1</c> chunks.
    /// </exception>
    public static uint ChunkCount(long length, uint chunkSize)
    {
        RequireChunkSize(chunkSize);

        if (length < 0)
        {
            throw new VaultFormatException(VaultErrorCode.IndexInvalid, $"A file length must not be negative (was {length}).");
        }

        checked
        {
            long count = (length + chunkSize - 1) / chunkSize;
            if (count < VaultLimits.MinChunkCount)
            {
                count = VaultLimits.MinChunkCount;
            }

            if (count > VaultLimits.MaxChunkCount)
            {
                throw new VaultFormatException(
                    VaultErrorCode.IndexInvalid,
                    $"A blob must not need more than {VaultLimits.MaxChunkCount} chunks (a length of {length} at a chunk size of {chunkSize} needs {count}).");
            }

            return (uint)count;
        }
    }

    /// <summary>Ciphertext length of a blob: <c>length + 16 * ChunkCount(length, chunkSize)</c>.</summary>
    /// <param name="length">Plaintext length in bytes.</param>
    /// <param name="chunkSize">Chunk size in bytes.</param>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.IndexInvalid"/> - see <see cref="ChunkCount"/>.</exception>
    public static long BlobLength(long length, uint chunkSize)
    {
        uint chunks = ChunkCount(length, chunkSize);
        checked
        {
            return length + ((long)TagSize * chunks);
        }
    }

    /// <summary>Writes the nonce of a chunk: <c>u32 chunkIndex (little-endian) || 8 zero bytes</c>.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="nonce12">Destination, exactly 12 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="nonce12"/> is not 12 bytes.</exception>
    public static void BuildNonce(uint chunkIndex, Span<byte> nonce12)
    {
        if (nonce12.Length != NonceSize)
        {
            throw new ArgumentException($"A chunk nonce is {NonceSize} bytes (was {nonce12.Length}).", nameof(nonce12));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(nonce12, chunkIndex);
        nonce12[4..].Clear();
    }

    /// <summary>
    /// Writes the chunk AAD: <c>"bastion/v1/chunk" || vaultId(16) || blobId(16) || u32 chunkIndex || u8 isLast</c>,
    /// 16 + 16 + 16 + 4 + 1 = 53 bytes.
    /// </summary>
    /// <param name="vaultId">The 16-byte vault id.</param>
    /// <param name="blobId">The 16-byte blob id.</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="isLast">True for the final chunk of the blob.</param>
    /// <param name="aad">Destination, exactly 53 bytes.</param>
    /// <exception cref="ArgumentException">A span has the wrong length.</exception>
    public static void BuildAad(ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId, uint chunkIndex, bool isLast, Span<byte> aad)
    {
        if (vaultId.Length != VaultIdSize)
        {
            throw new ArgumentException($"A vault id is {VaultIdSize} bytes (was {vaultId.Length}).", nameof(vaultId));
        }

        if (blobId.Length != BlobIdSize)
        {
            throw new ArgumentException($"A blob id is {BlobIdSize} bytes (was {blobId.Length}).", nameof(blobId));
        }

        if (aad.Length != AadSize)
        {
            throw new ArgumentException($"A chunk AAD is {AadSize} bytes (was {aad.Length}).", nameof(aad));
        }

        ChunkLabel.CopyTo(aad);
        vaultId.CopyTo(aad[16..]);
        blobId.CopyTo(aad[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(aad[48..52], chunkIndex);
        aad[52] = isLast ? (byte)1 : (byte)0;
    }

    /// <summary>Encrypts one chunk into <paramref name="ciphertextAndTag"/> (plaintext length + 16 bytes).</summary>
    /// <param name="aes">An AES-GCM instance keyed with the blob key.</param>
    /// <param name="vaultId">The 16-byte vault id.</param>
    /// <param name="blobId">The 16-byte blob id.</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="isLast">True for the final chunk of the blob.</param>
    /// <param name="plaintext">Chunk plaintext.</param>
    /// <param name="ciphertextAndTag">Destination for the ciphertext followed by the 16-byte tag.</param>
    /// <exception cref="ArgumentNullException"><paramref name="aes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The destination is not exactly the plaintext length plus 16 bytes.</exception>
    public static void EncryptChunk(AesGcm aes, ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId,
                                    uint chunkIndex, bool isLast, ReadOnlySpan<byte> plaintext, Span<byte> ciphertextAndTag)
    {
        ArgumentNullException.ThrowIfNull(aes);
        if (ciphertextAndTag.Length != plaintext.Length + TagSize)
        {
            throw new ArgumentException(
                $"The destination must be the plaintext length plus {TagSize} bytes (expected {plaintext.Length + TagSize}, was {ciphertextAndTag.Length}).",
                nameof(ciphertextAndTag));
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        BuildNonce(chunkIndex, nonce);
        BuildAad(vaultId, blobId, chunkIndex, isLast, aad);

        aes.Encrypt(nonce, plaintext, ciphertextAndTag[..plaintext.Length], ciphertextAndTag[plaintext.Length..], aad);
    }

    /// <summary>Authenticates and decrypts one chunk.</summary>
    /// <param name="aes">An AES-GCM instance keyed with the blob key.</param>
    /// <param name="vaultId">The 16-byte vault id.</param>
    /// <param name="blobId">The 16-byte blob id.</param>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="isLast">True for the final chunk of the blob.</param>
    /// <param name="ciphertextAndTag">Ciphertext followed by its 16-byte tag.</param>
    /// <param name="plaintext">Destination, <c>ciphertextAndTag.Length - 16</c> bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="aes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The input is shorter than a tag or the destination has the wrong length.</exception>
    /// <exception cref="VaultIntegrityException"><see cref="VaultErrorCode.DataCorrupt"/> when the tag does not authenticate.</exception>
    public static void DecryptChunk(AesGcm aes, ReadOnlySpan<byte> vaultId, ReadOnlySpan<byte> blobId,
                                    uint chunkIndex, bool isLast, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext)
    {
        ArgumentNullException.ThrowIfNull(aes);
        if (ciphertextAndTag.Length < TagSize)
        {
            throw new ArgumentException($"A chunk carries at least a {TagSize}-byte tag (was {ciphertextAndTag.Length}).", nameof(ciphertextAndTag));
        }

        if (plaintext.Length != ciphertextAndTag.Length - TagSize)
        {
            throw new ArgumentException(
                $"The destination must be the ciphertext length minus {TagSize} bytes (expected {ciphertextAndTag.Length - TagSize}, was {plaintext.Length}).",
                nameof(plaintext));
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        BuildNonce(chunkIndex, nonce);
        BuildAad(vaultId, blobId, chunkIndex, isLast, aad);

        try
        {
            aes.Decrypt(nonce, ciphertextAndTag[..plaintext.Length], ciphertextAndTag[plaintext.Length..], plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            plaintext.Clear();
            throw new VaultIntegrityException(
                VaultErrorCode.DataCorrupt,
                "a chunk of this file failed authentication: the vault data has been altered or damaged",
                ex)
            {
                ChunkIndex = chunkIndex,
            };
        }
    }

    private static void RequireChunkSize(uint chunkSize)
    {
        if (chunkSize is < VaultLimits.MinChunkSize or > VaultLimits.MaxChunkSize || !uint.IsPow2(chunkSize))
        {
            throw new VaultFormatException(
                VaultErrorCode.IndexInvalid,
                $"chunkSize must be a power of two between {VaultLimits.MinChunkSize} and {VaultLimits.MaxChunkSize} (was {chunkSize}).");
        }
    }
}
