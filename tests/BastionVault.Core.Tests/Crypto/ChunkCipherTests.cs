using System.Security.Cryptography;
using BastionVault.Core.Crypto;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>FORMAT.md section 2.7: chunk framing, the nonce and AAD construction, and the tamper matrix.</summary>
public sealed class ChunkCipherTests
{
    private const uint ChunkSize = 65536;

    private static byte[] VaultId => Filled(16, 0xA1);

    private static byte[] BlobId => Filled(16, 0xB2);

    private static byte[] BlobKey => Filled(32, 0xC3);

    [Theory]
    [InlineData(0L, 1u)]                    // an empty file still has one empty chunk
    [InlineData(1L, 1u)]
    [InlineData(65535L, 1u)]
    [InlineData(65536L, 1u)]
    [InlineData(65537L, 2u)]
    [InlineData(131072L, 2u)]
    [InlineData(131073L, 3u)]
    public void ChunkCount_FollowsTheFormat(long length, uint expected)
    {
        Assert.Equal(expected, ChunkCipher.ChunkCount(length, ChunkSize));
    }

    [Theory]
    [InlineData(0L, 16L)]
    [InlineData(1L, 17L)]
    [InlineData(65536L, 65552L)]
    [InlineData(65537L, 65569L)]
    public void BlobLength_AddsOneTagPerChunk(long length, long expected)
    {
        Assert.Equal(expected, ChunkCipher.BlobLength(length, ChunkSize));
    }

    [Theory]
    [InlineData(0u)]         // not a power of two and below the minimum
    [InlineData(32768u)]     // below the minimum
    [InlineData(98304u)]     // in range but not a power of two
    [InlineData(134217728u)] // above the maximum
    public void ChunkCount_RejectsAnInvalidChunkSize(uint chunkSize)
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => ChunkCipher.ChunkCount(1024, chunkSize));

        Assert.Equal(VaultErrorCode.IndexInvalid, error.Code);
    }

    [Fact]
    public void ChunkCount_RejectsABlobThatWouldNeedMoreThanFourBillionChunks()
    {
        // 2^32 chunks of 64 KiB is 2^48 bytes, one more than the largest allowed file length.
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => ChunkCipher.ChunkCount(1L << 48, ChunkSize));

        Assert.Equal(VaultErrorCode.IndexInvalid, error.Code);
    }

    [Fact]
    public void ChunkCount_RejectsANegativeLength()
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => ChunkCipher.ChunkCount(-1, ChunkSize));

        Assert.Equal(VaultErrorCode.IndexInvalid, error.Code);
    }

    [Fact]
    public void BuildNonce_IsTheLittleEndianIndexFollowedByZeroes()
    {
        Span<byte> nonce = stackalloc byte[12];
        nonce.Fill(0xFF);
        ChunkCipher.BuildNonce(0x01020304, nonce);

        Assert.Equal("040302010000000000000000", Convert.ToHexStringLower(nonce));
    }

    [Fact]
    public void BuildAad_IsFiftyThreeBytesInTheDocumentedOrder()
    {
        Span<byte> aad = stackalloc byte[53];
        ChunkCipher.BuildAad(VaultId, BlobId, 7, isLast: true, aad);

        Assert.Equal("bastion/v1/chunk"u8.ToArray(), aad[..16].ToArray());
        Assert.Equal(VaultId, aad[16..32].ToArray());
        Assert.Equal(BlobId, aad[32..48].ToArray());
        Assert.Equal("07000000", Convert.ToHexStringLower(aad[48..52]));
        Assert.Equal((byte)1, aad[52]);
    }

    [Fact]
    public void BuildAad_RejectsWronglySizedSpans()
    {
        byte[] vaultId = VaultId;
        byte[] blobId = BlobId;

        Assert.Throws<ArgumentException>(() => ChunkCipher.BuildAad(new byte[15], blobId, 0, false, new byte[53]));
        Assert.Throws<ArgumentException>(() => ChunkCipher.BuildAad(vaultId, new byte[17], 0, false, new byte[53]));
        Assert.Throws<ArgumentException>(() => ChunkCipher.BuildAad(vaultId, blobId, 0, false, new byte[52]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData((int)ChunkSize)]
    public void EncryptChunk_RoundTrips(int plaintextLength)
    {
        byte[] plaintext = new byte[plaintextLength];
        Random.Shared.NextBytes(plaintext);

        byte[] chunk = new byte[plaintextLength + ChunkCipher.TagSize];
        using (AesGcm aes = new(BlobKey, ChunkCipher.TagSize))
        {
            ChunkCipher.EncryptChunk(aes, VaultId, BlobId, 0, isLast: true, plaintext, chunk);
        }

        byte[] recovered = new byte[plaintextLength];
        using (AesGcm aes = new(BlobKey, ChunkCipher.TagSize))
        {
            ChunkCipher.DecryptChunk(aes, VaultId, BlobId, 0, isLast: true, chunk, recovered);
        }

        Assert.Equal(plaintext, recovered);
    }

    [Fact]
    public void EncryptChunk_RoundTripsAWholeMultiChunkBlob()
    {
        byte[] plaintext = new byte[(2 * (int)ChunkSize) + 12345];
        Random.Shared.NextBytes(plaintext);

        uint chunks = ChunkCipher.ChunkCount(plaintext.Length, ChunkSize);
        Assert.Equal(3u, chunks);

        byte[] blob = new byte[ChunkCipher.BlobLength(plaintext.Length, ChunkSize)];
        using (AesGcm aes = new(BlobKey, ChunkCipher.TagSize))
        {
            int source = 0;
            int destination = 0;
            for (uint i = 0; i < chunks; i++)
            {
                int take = Math.Min((int)ChunkSize, plaintext.Length - source);
                ChunkCipher.EncryptChunk(
                    aes, VaultId, BlobId, i, i == chunks - 1,
                    plaintext.AsSpan(source, take), blob.AsSpan(destination, take + ChunkCipher.TagSize));
                source += take;
                destination += take + ChunkCipher.TagSize;
            }
        }

        byte[] recovered = new byte[plaintext.Length];
        using (AesGcm aes = new(BlobKey, ChunkCipher.TagSize))
        {
            int source = 0;
            int destination = 0;
            for (uint i = 0; i < chunks; i++)
            {
                int take = Math.Min((int)ChunkSize, plaintext.Length - destination);
                ChunkCipher.DecryptChunk(
                    aes, VaultId, BlobId, i, i == chunks - 1,
                    blob.AsSpan(source, take + ChunkCipher.TagSize), recovered.AsSpan(destination, take));
                source += take + ChunkCipher.TagSize;
                destination += take;
            }
        }

        Assert.Equal(plaintext, recovered);
    }

    /// <summary>
    /// The tamper matrix of FORMAT.md section 2.7: a flipped ciphertext byte, a flipped tag byte and every
    /// AAD field that binds a chunk to its place must each fail with <see cref="VaultErrorCode.DataCorrupt"/>.
    /// </summary>
    [Theory]
    [InlineData(Tamper.Ciphertext)]
    [InlineData(Tamper.Tag)]
    [InlineData(Tamper.ChunkIndex)]
    [InlineData(Tamper.IsLast)]
    [InlineData(Tamper.BlobId)]
    [InlineData(Tamper.VaultId)]
    [InlineData(Tamper.Key)]
    public void DecryptChunk_RejectsEveryTamperedInput(Tamper tamper)
    {
        const uint index = 3;
        byte[] plaintext = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        byte[] chunk = new byte[plaintext.Length + ChunkCipher.TagSize];

        using (AesGcm aes = new(BlobKey, ChunkCipher.TagSize))
        {
            ChunkCipher.EncryptChunk(aes, VaultId, BlobId, index, isLast: false, plaintext, chunk);
        }

        byte[] vaultId = VaultId;
        byte[] blobId = BlobId;
        byte[] key = BlobKey;
        uint useIndex = index;
        bool useIsLast = false;

        switch (tamper)
        {
            case Tamper.Ciphertext:
                chunk[0] ^= 0x01;
                break;
            case Tamper.Tag:
                chunk[^1] ^= 0x01;
                break;
            case Tamper.ChunkIndex:
                useIndex = index + 1;
                break;
            case Tamper.IsLast:
                useIsLast = true;
                break;
            case Tamper.BlobId:
                blobId[5] ^= 0x01;
                break;
            case Tamper.VaultId:
                vaultId[5] ^= 0x01;
                break;
            case Tamper.Key:
                key[0] ^= 0x01;
                break;
        }

        byte[] recovered = new byte[plaintext.Length];
        using AesGcm decryptor = new(key, ChunkCipher.TagSize);

        VaultIntegrityException error = Assert.Throws<VaultIntegrityException>(
            () => ChunkCipher.DecryptChunk(decryptor, vaultId, blobId, useIndex, useIsLast, chunk, recovered));

        Assert.Equal(VaultErrorCode.DataCorrupt, error.Code);
        Assert.Equal(useIndex, error.ChunkIndex);
        Assert.All(recovered, b => Assert.Equal((byte)0, b));
    }

    /// <summary>The chunks of a blob may not be swapped: each one is bound to its index.</summary>
    [Fact]
    public void DecryptChunk_RejectsSwappedChunks()
    {
        byte[] first = "first-chunk-payload"u8.ToArray();
        byte[] second = "secondchunkpayload!"u8.ToArray();
        byte[] chunk0 = new byte[first.Length + ChunkCipher.TagSize];
        byte[] chunk1 = new byte[second.Length + ChunkCipher.TagSize];

        using AesGcm aes = new(BlobKey, ChunkCipher.TagSize);
        ChunkCipher.EncryptChunk(aes, VaultId, BlobId, 0, isLast: false, first, chunk0);
        ChunkCipher.EncryptChunk(aes, VaultId, BlobId, 1, isLast: true, second, chunk1);

        byte[] recovered = new byte[first.Length];

        VaultIntegrityException error = Assert.Throws<VaultIntegrityException>(
            () => ChunkCipher.DecryptChunk(aes, VaultId, BlobId, 0, isLast: false, chunk1, recovered));

        Assert.Equal(VaultErrorCode.DataCorrupt, error.Code);
    }

    [Fact]
    public void EncryptChunk_RejectsAWronglySizedDestination()
    {
        byte[] plaintext = new byte[10];
        using AesGcm aes = new(BlobKey, ChunkCipher.TagSize);

        Assert.Throws<ArgumentException>(() => ChunkCipher.EncryptChunk(aes, VaultId, BlobId, 0, true, plaintext, new byte[25]));
    }

    /// <summary>The tamper cases of <see cref="DecryptChunk_RejectsEveryTamperedInput"/>.</summary>
    public enum Tamper
    {
        /// <summary>One ciphertext byte is flipped.</summary>
        Ciphertext,

        /// <summary>One tag byte is flipped.</summary>
        Tag,

        /// <summary>The chunk is decrypted under a different index.</summary>
        ChunkIndex,

        /// <summary>The chunk is decrypted with the opposite last-chunk flag.</summary>
        IsLast,

        /// <summary>The chunk is decrypted under a different blob id.</summary>
        BlobId,

        /// <summary>The chunk is decrypted under a different vault id.</summary>
        VaultId,

        /// <summary>The chunk is decrypted under a different blob key.</summary>
        Key,
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }
}
