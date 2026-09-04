using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Format;

/// <summary>Covers the index ladder of FORMAT.md section 4.2 and the obfuscation ladder of section 5.</summary>
public sealed class PadLadderTests
{
    /// <summary>One kibibyte.</summary>
    private const long KiB = 1024;

    /// <summary>One mebibyte.</summary>
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(0, 64 * KiB)]
    [InlineData(1, 64 * KiB)]
    [InlineData((64 * KiB) - 1, 64 * KiB)]
    [InlineData(64 * KiB, 64 * KiB)]
    [InlineData((64 * KiB) + 1, 128 * KiB)]
    [InlineData(128 * KiB, 128 * KiB)]
    [InlineData((128 * KiB) + 1, 256 * KiB)]
    [InlineData(256 * KiB, 256 * KiB)]
    [InlineData((256 * KiB) + 1, 512 * KiB)]
    [InlineData(512 * KiB, 512 * KiB)]
    [InlineData((512 * KiB) + 1, MiB)]
    [InlineData(MiB, MiB)]
    [InlineData(MiB + 1, 2 * MiB)]
    [InlineData(2 * MiB, 2 * MiB)]
    [InlineData((2 * MiB) + 1, 3 * MiB)]
    [InlineData(63 * MiB, 63 * MiB)]
    [InlineData((63 * MiB) + 1, 64 * MiB)]
    [InlineData(64 * MiB, 64 * MiB)]
    public void Index_FollowsTheLadder(long unpaddedLength, long expected)
    {
        Assert.Equal(expected, PadLadder.Index(unpaddedLength));
    }

    [Fact]
    public void Index_IsMonotonicAndNeverShrinksTheInput()
    {
        long previous = 0;
        foreach (long n in Probes().Distinct().Order())
        {
            long padded = PadLadder.Index(n);
            Assert.True(padded >= n, $"PadLadder.Index({n}) = {padded} is smaller than its input.");
            Assert.True(padded >= previous, $"PadLadder.Index({n}) = {padded} went backwards from {previous}.");
            previous = padded;
        }
    }

    [Fact]
    public void Index_RejectsANegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PadLadder.Index(-1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, MiB)]
    [InlineData(MiB - 1, MiB)]
    [InlineData(MiB, MiB)]
    [InlineData(MiB + 1, 2 * MiB)]
    [InlineData(15 * MiB, 15 * MiB)]
    [InlineData(16 * MiB, 16 * MiB)]                    // step is still 1 MiB (2^24 / 16)
    [InlineData((16 * MiB) + 1, 17 * MiB)]
    [InlineData(32 * MiB, 32 * MiB)]                    // step becomes 2 MiB (2^25 / 16)
    [InlineData((32 * MiB) + 1, 34 * MiB)]
    [InlineData(33 * MiB, 34 * MiB)]
    [InlineData(100 * MiB, 100 * MiB)]                  // step 4 MiB, and 100 is a multiple of 4
    [InlineData((100 * MiB) + 1, 104 * MiB)]
    [InlineData(1024 * MiB, 1024 * MiB)]                // step 64 MiB
    [InlineData((1024 * MiB) + 1, (1024 + 64) * MiB)]
    public void Obfuscation_FollowsTheLadder(long dataLength, long expected)
    {
        Assert.Equal(expected, PadLadder.Obfuscation(dataLength));
    }

    [Fact]
    public void Obfuscation_AlwaysLandsOnAMultipleOfItsStep()
    {
        foreach (long n in Probes())
        {
            if (n == 0)
            {
                continue;
            }

            long padded = PadLadder.Obfuscation(n);
            long step = Math.Max(MiB, HighestPowerOfTwo(n) / 16);

            Assert.True(padded >= n, $"PadLadder.Obfuscation({n}) = {padded} is smaller than its input.");
            Assert.Equal(0, padded % step);
            Assert.True(padded - n < step, $"PadLadder.Obfuscation({n}) = {padded} overshoots by a whole step.");
        }
    }

    [Fact]
    public void Obfuscation_RejectsANegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PadLadder.Obfuscation(-1));
    }

    /// <summary>A spread of interesting sizes: powers of two and their neighbours, plus round numbers.</summary>
    private static IEnumerable<long> Probes()
    {
        yield return 0;
        for (int bit = 0; bit < 42; bit++)
        {
            long value = 1L << bit;
            yield return value - 1;
            yield return value;
            yield return value + 1;
        }
    }

    /// <summary>The largest power of two not greater than the value.</summary>
    /// <param name="value">A positive value.</param>
    private static long HighestPowerOfTwo(long value)
    {
        long result = 1;
        while (result <= value / 2)
        {
            result *= 2;
        }

        return result;
    }
}
