using System.Security.Cryptography;
using BastionVault.Core.Crypto;

namespace BastionVault.Core.Session;

/// <summary>
/// Everything the session knows about the content of one file entry: which bytes to read, under which
/// blob identity to authenticate them, and whether the next save may copy them verbatim.
/// </summary>
internal sealed class BlobRef
{
    /// <summary>The 16-byte blob id the ciphertext was produced under.</summary>
    public required byte[] BlobId { get; init; }

    /// <summary>Where the ciphertext lives.</summary>
    public required IBlobSource Source { get; init; }

    /// <summary>Plaintext length of the file in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Chunk size the blob was written with.</summary>
    public required uint ChunkSize { get; init; }

    /// <summary>SHA-256 over the whole blob ciphertext (FORMAT.md section 2.8).</summary>
    public required byte[] BlobHash { get; init; }

    /// <summary>
    /// True when the next save must re-encrypt this content under a fresh blob id: an in-vault copy
    /// shares its source ciphertext with the original, and a blob is never referenced twice.
    /// </summary>
    public bool RequiresReencrypt { get; init; }

    /// <summary>True when the ciphertext is not yet part of the vault file (imported or copied).</summary>
    public bool IsPending => Source is not StoredBlobSource || RequiresReencrypt;

    /// <summary>Ciphertext length of the blob.</summary>
    public long BlobLength => ChunkCipher.BlobLength(Length, ChunkSize);

    /// <summary>Returns a copy of this reference that a save must re-encrypt.</summary>
    public BlobRef AsCopy() => new()
    {
        BlobId = BlobId,
        Source = Source,
        Length = Length,
        ChunkSize = ChunkSize,
        BlobHash = BlobHash,
        RequiresReencrypt = true,
    };
}

/// <summary>
/// Authenticates and decrypts the chunks of one blob. The instance owns the derived blob key and the
/// AES-GCM instance built from it, so it must be disposed.
/// </summary>
internal sealed class BlobReader : IDisposable
{
    private readonly IBlobSource _source;
    private readonly byte[] _vaultId;
    private readonly byte[] _blobId;
    private readonly long _length;
    private readonly uint _chunkSize;
    private readonly KeyMaterial _blobKey;
    private readonly AesGcm _aes;
    private readonly string? _vaultPath;

    /// <summary>Opens a reader over a blob.</summary>
    /// <param name="source">Where the ciphertext lives.</param>
    /// <param name="crypto">Keys of the session the blob belongs to.</param>
    /// <param name="blobId">The 16-byte blob id.</param>
    /// <param name="length">Plaintext length.</param>
    /// <param name="chunkSize">Chunk size of the blob.</param>
    /// <param name="vaultPath">In-vault path used in integrity errors, when known.</param>
    public BlobReader(IBlobSource source, VaultCrypto crypto, byte[] blobId, long length, uint chunkSize, string? vaultPath)
    {
        ArgumentNullException.ThrowIfNull(crypto);

        _source = source;
        _vaultId = crypto.VaultId;
        _blobId = blobId;
        _length = length;
        _chunkSize = chunkSize;
        _vaultPath = vaultPath;
        ChunkCount = ChunkCipher.ChunkCount(length, chunkSize);

        _blobKey = VaultKeys.DeriveBlobKey(crypto.VaultKey.Span, blobId);
        try
        {
            _aes = new AesGcm(_blobKey.Span, ChunkCipher.TagSize);
        }
        catch
        {
            _blobKey.Dispose();
            throw;
        }
    }

    /// <summary>Number of chunks in the blob; at least one.</summary>
    public uint ChunkCount { get; }

    /// <summary>Plaintext length of the blob.</summary>
    public long Length => _length;

    /// <summary>Chunk size of the blob.</summary>
    public uint ChunkSize => _chunkSize;

    /// <summary>
    /// Plaintext length of the largest chunk this blob actually has. <see cref="ChunkSize"/> is an index
    /// field an attacker picks freely anywhere in 64 KiB .. 64 MiB, so a buffer sized from it costs up to
    /// 64 MiB per one-byte file; the blob's own length bounds it.
    /// </summary>
    public int MaxChunkPlaintextLength => _length <= 0 ? 0 : (int)Math.Min(_length, _chunkSize);

    /// <summary>Ciphertext length of the largest chunk this blob actually has.</summary>
    public int MaxChunkCiphertextLength => MaxChunkPlaintextLength + ChunkCipher.TagSize;

    /// <summary>Plaintext length of one chunk.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    public int PlaintextLengthOf(uint chunkIndex)
    {
        long start = (long)chunkIndex * _chunkSize;
        long remaining = _length - start;
        return remaining <= 0 ? 0 : (int)Math.Min(remaining, _chunkSize);
    }

    /// <summary>Ciphertext length of one chunk (plaintext plus tag).</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    public int CiphertextLengthOf(uint chunkIndex) => PlaintextLengthOf(chunkIndex) + ChunkCipher.TagSize;

    /// <summary>Blob-relative offset of one chunk's ciphertext.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    public long CiphertextOffsetOf(uint chunkIndex) => (long)chunkIndex * (_chunkSize + ChunkCipher.TagSize);

    /// <summary>Reads one chunk's ciphertext without authenticating it.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="destination">Buffer of exactly <see cref="CiphertextLengthOf"/> bytes.</param>
    public void ReadCiphertext(uint chunkIndex, Span<byte> destination) =>
        _source.Read(CiphertextOffsetOf(chunkIndex), destination);

    /// <summary>Authenticates and decrypts one chunk that has already been read.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="ciphertextAndTag">The chunk's ciphertext followed by its tag.</param>
    /// <param name="plaintext">Destination for the plaintext.</param>
    /// <exception cref="VaultIntegrityException"><see cref="VaultErrorCode.DataCorrupt"/> when the tag fails.</exception>
    public void DecryptChunk(uint chunkIndex, ReadOnlySpan<byte> ciphertextAndTag, Span<byte> plaintext)
    {
        try
        {
            ChunkCipher.DecryptChunk(_aes, _vaultId, _blobId, chunkIndex, chunkIndex == ChunkCount - 1, ciphertextAndTag, plaintext);
        }
        catch (VaultIntegrityException ex) when (ex.VaultPath is null && _vaultPath is not null)
        {
            throw new VaultIntegrityException(ex.Code, ex.Message, ex.InnerException)
            {
                VaultPath = _vaultPath,
                ChunkIndex = ex.ChunkIndex ?? chunkIndex,
            };
        }
    }

    /// <summary>Reads, authenticates and decrypts one chunk.</summary>
    /// <param name="chunkIndex">Zero-based chunk index.</param>
    /// <param name="ciphertextBuffer">Scratch buffer of at least <see cref="CiphertextLengthOf"/> bytes.</param>
    /// <param name="plaintext">Destination of at least <see cref="PlaintextLengthOf"/> bytes.</param>
    /// <returns>The number of plaintext bytes written.</returns>
    public int ReadPlaintextChunk(uint chunkIndex, Span<byte> ciphertextBuffer, Span<byte> plaintext)
    {
        int cipherLength = CiphertextLengthOf(chunkIndex);
        int plainLength = cipherLength - ChunkCipher.TagSize;
        Span<byte> cipher = ciphertextBuffer[..cipherLength];
        ReadCiphertext(chunkIndex, cipher);
        DecryptChunk(chunkIndex, cipher, plaintext[..plainLength]);
        return plainLength;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _aes.Dispose();
        _blobKey.Dispose();
    }
}
