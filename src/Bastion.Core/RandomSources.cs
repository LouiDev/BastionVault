using System.Numerics;
using System.Security.Cryptography;

namespace Bastion.Core;

/// <summary>The production randomness seam: the OS CSPRNG.</summary>
public sealed class SystemRandomSource : IRandomSource
{
    /// <summary>The shared instance; the class is stateless and thread-safe.</summary>
    public static readonly SystemRandomSource Instance = new();

    /// <inheritdoc />
    public void Fill(Span<byte> buffer) => RandomNumberGenerator.Fill(buffer);
}

/// <summary>
/// A reproducible pseudo-random sequence for tests and golden fixtures. Never use it for real vaults.
/// </summary>
/// <remarks>
/// <para>
/// The generator is <c>xoshiro256**</c> (Blackman/Vigna, 2018). The 256-bit state is seeded by running
/// SplitMix64 four times over the constructor seed, which is the seeding procedure the authors prescribe
/// and which gives a well-distributed state even for seed 0.
/// </para>
/// <para>
/// One step produces 64 bits, written to the output little-endian. Leftover bytes of a step carry over to
/// the next <see cref="Fill(Span{byte})"/> call, so the instance emits one byte stream that depends only on
/// the seed and on the total number of bytes requested, never on how the requests were chunked. Instances
/// with equal seeds therefore produce identical bytes on every machine and in every run.
/// </para>
/// </remarks>
public sealed class DeterministicRandomSource : IRandomSource
{
    private const ulong SplitMixIncrement = 0x9E3779B97F4A7C15UL;

    private readonly Lock _gate = new();
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;
    private ulong _carry;
    private int _carryBytes;

    /// <summary>Creates a source that always produces the same byte stream for the same seed.</summary>
    /// <param name="seed">Seed of the generator.</param>
    public DeterministicRandomSource(ulong seed)
    {
        ulong state = seed;
        _s0 = SplitMix64(ref state);
        _s1 = SplitMix64(ref state);
        _s2 = SplitMix64(ref state);
        _s3 = SplitMix64(ref state);
    }

    /// <inheritdoc />
    public void Fill(Span<byte> buffer)
    {
        lock (_gate)
        {
            while (!buffer.IsEmpty)
            {
                if (_carryBytes == 0)
                {
                    _carry = Next();
                    _carryBytes = sizeof(ulong);
                }

                int take = Math.Min(_carryBytes, buffer.Length);
                for (int i = 0; i < take; i++)
                {
                    buffer[i] = (byte)_carry;
                    _carry >>= 8;
                }

                _carryBytes -= take;
                buffer = buffer[take..];
            }
        }
    }

    private ulong Next()
    {
        ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += SplitMixIncrement;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
