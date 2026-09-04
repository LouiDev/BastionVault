using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace BastionVault.Core.Crypto;

/// <summary>BLAKE2b, the hash Argon2 is built on (RFC 7693).</summary>
public static class Blake2b
{
    /// <summary>Hashes <paramref name="input"/> into <paramref name="output"/> without a key.</summary>
    /// <param name="input">Message to hash.</param>
    /// <param name="output">Destination; its length (1 .. 64 bytes) selects the digest size.</param>
    /// <exception cref="ArgumentException"><paramref name="output"/> is empty or longer than 64 bytes.</exception>
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output) => Hash(default, input, output);

    /// <summary>Hashes <paramref name="input"/> into <paramref name="output"/> in keyed mode.</summary>
    /// <param name="key">Key, up to 64 bytes.</param>
    /// <param name="input">Message to hash.</param>
    /// <param name="output">Destination; its length (1 .. 64 bytes) selects the digest size.</param>
    /// <exception cref="ArgumentException"><paramref name="output"/> is empty or longer than 64 bytes, or the key is longer than 64 bytes.</exception>
    public static void Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Blake2bHasher hasher = new(output.Length, key);
        try
        {
            hasher.Update(input);
            hasher.Final(output);
        }
        finally
        {
            hasher.Clear();
        }
    }
}

/// <summary>
/// Incremental BLAKE2b state (RFC 7693). A mutable struct so that Argon2 can hash long
/// concatenations without allocating; always used through a local variable.
/// </summary>
internal struct Blake2bHasher
{
    /// <summary>Size of one compression input block in bytes.</summary>
    internal const int BlockSize = 128;

    /// <summary>Largest digest BLAKE2b produces.</summary>
    internal const int MaxDigestLength = 64;

    /// <summary>Largest key BLAKE2b accepts.</summary>
    internal const int MaxKeyLength = 64;

    private static ReadOnlySpan<ulong> Iv =>
    [
        0x6A09E667F3BCC908UL, 0xBB67AE8584CAA73BUL, 0x3C6EF372FE94F82BUL, 0xA54FF53A5F1D36F1UL,
        0x510E527FADE682D1UL, 0x9B05688C2B3E6C1FUL, 0x1F83D9ABFB41BD6BUL, 0x5BE0CD19137E2179UL,
    ];

    private static ReadOnlySpan<byte> Sigma =>
    [
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        14, 10,  4,  8,  9, 15, 13,  6,  1, 12,  0,  2, 11,  7,  5,  3,
        11,  8, 12,  0,  5,  2, 15, 13, 10, 14,  3,  6,  7,  1,  9,  4,
         7,  9,  3,  1, 13, 12, 11, 14,  2,  6,  5, 10,  4,  0, 15,  8,
         9,  0,  5,  7,  2,  4, 10, 15, 14,  1, 11, 12,  6,  8,  3, 13,
         2, 12,  6, 10,  0, 11,  8,  3,  4, 13,  7,  5, 15, 14,  1,  9,
        12,  5,  1, 15, 14, 13,  4, 10,  0,  7,  6,  3,  9,  2,  8, 11,
        13, 11,  7, 14, 12,  1,  3,  9,  5,  0, 15,  4,  8,  6,  2, 10,
         6, 15, 14,  9, 11,  3,  0,  8, 12,  2, 13,  7,  1,  4, 10,  5,
        10,  2,  8,  4,  7,  6,  1,  5, 15, 11,  9, 14,  3, 12, 13,  0,
         0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15,
        14, 10,  4,  8,  9, 15, 13,  6,  1, 12,  0,  2, 11,  7,  5,  3,
    ];

    [InlineArray(8)]
    private struct ChainingValue
    {
        private ulong _element0;
    }

    [InlineArray(BlockSize)]
    private struct InputBuffer
    {
        private byte _element0;
    }

    private ChainingValue _h;
    private InputBuffer _buffer;
    private ulong _counterLow;
    private ulong _counterHigh;
    private int _bufferFilled;
    private readonly int _digestLength;

    /// <summary>Starts a hash with the given digest length and optional key.</summary>
    /// <param name="digestLength">Digest length in bytes, 1 .. 64.</param>
    /// <param name="key">Key, empty for unkeyed hashing, at most 64 bytes.</param>
    internal Blake2bHasher(int digestLength, ReadOnlySpan<byte> key)
    {
        if (digestLength is < 1 or > MaxDigestLength)
        {
            throw new ArgumentException($"A BLAKE2b digest must be 1 .. {MaxDigestLength} bytes (was {digestLength}).", nameof(digestLength));
        }

        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException($"A BLAKE2b key must be at most {MaxKeyLength} bytes (was {key.Length}).", nameof(key));
        }

        _digestLength = digestLength;
        _counterLow = 0;
        _counterHigh = 0;
        _bufferFilled = 0;

        Span<ulong> h = _h;
        Iv.CopyTo(h);
        h[0] ^= 0x0101_0000UL ^ ((ulong)(uint)key.Length << 8) ^ (uint)digestLength;

        if (!key.IsEmpty)
        {
            Span<byte> buffer = _buffer;
            key.CopyTo(buffer);
            buffer[key.Length..].Clear();
            _bufferFilled = BlockSize;
        }
    }

    /// <summary>Absorbs more message bytes.</summary>
    /// <param name="data">Bytes to absorb.</param>
    internal void Update(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        Span<byte> buffer = _buffer;
        int free = BlockSize - _bufferFilled;

        // A full block is compressed only once more input is known to follow: the final block
        // of a message must be compressed with the last-block flag set.
        if (data.Length > free)
        {
            data[..free].CopyTo(buffer[_bufferFilled..]);
            IncrementCounter(BlockSize);
            Compress(buffer, last: false);
            _bufferFilled = 0;
            data = data[free..];

            while (data.Length > BlockSize)
            {
                IncrementCounter(BlockSize);
                Compress(data[..BlockSize], last: false);
                data = data[BlockSize..];
            }
        }

        data.CopyTo(buffer[_bufferFilled..]);
        _bufferFilled += data.Length;
    }

    /// <summary>Finishes the hash and writes the digest.</summary>
    /// <param name="output">Destination, exactly the digest length passed to the constructor.</param>
    internal void Final(Span<byte> output)
    {
        if (output.Length != _digestLength)
        {
            throw new ArgumentException($"The output must be exactly {_digestLength} bytes (was {output.Length}).", nameof(output));
        }

        Span<byte> buffer = _buffer;
        IncrementCounter((uint)_bufferFilled);
        buffer[_bufferFilled..].Clear();
        Compress(buffer, last: true);

        Span<byte> digest = stackalloc byte[MaxDigestLength];
        Span<ulong> h = _h;
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(digest.Slice(i * 8, 8), h[i]);
        }

        digest[.._digestLength].CopyTo(output);
        CryptographicOperations.ZeroMemory(digest);
    }

    /// <summary>Zeroes the chaining value and the input buffer.</summary>
    internal void Clear()
    {
        Span<ulong> h = _h;
        h.Clear();
        Span<byte> buffer = _buffer;
        buffer.Clear();
        _counterLow = 0;
        _counterHigh = 0;
        _bufferFilled = 0;
    }

    private void IncrementCounter(ulong amount)
    {
        _counterLow += amount;
        if (_counterLow < amount)
        {
            _counterHigh++;
        }
    }

    private void Compress(ReadOnlySpan<byte> block, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
        {
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
        }

        Span<ulong> h = _h;
        Span<ulong> v = stackalloc ulong[16];
        h.CopyTo(v);
        Iv.CopyTo(v[8..]);
        v[12] ^= _counterLow;
        v[13] ^= _counterHigh;
        if (last)
        {
            v[14] = ~v[14];
        }

        for (int round = 0; round < 12; round++)
        {
            ReadOnlySpan<byte> s = Sigma.Slice(round * 16, 16);
            Mix(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            Mix(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            Mix(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            Mix(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            Mix(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            Mix(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            Mix(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            Mix(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (int i = 0; i < 8; i++)
        {
            h[i] ^= v[i] ^ v[i + 8];
        }

        m.Clear();
        v.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Mix(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }
}
