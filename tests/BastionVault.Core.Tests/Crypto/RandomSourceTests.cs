namespace BastionVault.Core.Tests.Crypto;

/// <summary>The two randomness seams of API.md: the OS CSPRNG and the reproducible test generator.</summary>
public sealed class RandomSourceTests
{
    /// <summary>
    /// Frozen output of the documented generator (xoshiro256** seeded by four SplitMix64 steps). The golden
    /// fixtures depend on this byte stream, so it must never change silently.
    /// </summary>
    [Theory]
    [InlineData(0UL, "b4f275cb365fec992a455649781f6ebfe0e633499d845f1a2c2d2d26f194a56a")]
    [InlineData(1UL, "c510c70f6daff2b3ea4c364796553b8514452a085697f892a7a366c27b1c2e64")]
    public void DeterministicRandomSource_MatchesTheFrozenStream(ulong seed, string expected)
    {
        byte[] buffer = new byte[32];
        new DeterministicRandomSource(seed).Fill(buffer);

        Assert.Equal(expected, Convert.ToHexStringLower(buffer));
    }

    [Fact]
    public void DeterministicRandomSource_IsReproducibleForTheSameSeed()
    {
        byte[] first = new byte[256];
        byte[] second = new byte[256];

        new DeterministicRandomSource(0x0123456789ABCDEF).Fill(first);
        new DeterministicRandomSource(0x0123456789ABCDEF).Fill(second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeterministicRandomSource_DiffersBetweenSeeds()
    {
        byte[] first = new byte[64];
        byte[] second = new byte[64];

        new DeterministicRandomSource(0).Fill(first);
        new DeterministicRandomSource(1).Fill(second);

        Assert.NotEqual(first, second);
    }

    /// <summary>The instance emits one byte stream; how a caller chunks its requests must not change it.</summary>
    [Fact]
    public void DeterministicRandomSource_IsIndependentOfTheRequestChunking()
    {
        byte[] whole = new byte[100];
        new DeterministicRandomSource(42).Fill(whole);

        byte[] pieces = new byte[100];
        DeterministicRandomSource chunked = new(42);
        int offset = 0;
        foreach (int size in new[] { 1, 3, 8, 5, 13, 2, 64, 4 })
        {
            chunked.Fill(pieces.AsSpan(offset, size));
            offset += size;
        }

        Assert.Equal(100, offset);
        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void DeterministicRandomSource_AdvancesBetweenCalls()
    {
        DeterministicRandomSource source = new(5);
        byte[] first = new byte[32];
        byte[] second = new byte[32];
        source.Fill(first);
        source.Fill(second);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DeterministicRandomSource_AcceptsAnEmptyBuffer()
    {
        DeterministicRandomSource source = new(5);
        source.Fill(Span<byte>.Empty);

        byte[] buffer = new byte[8];
        source.Fill(buffer);

        byte[] reference = new byte[8];
        new DeterministicRandomSource(5).Fill(reference);

        Assert.Equal(reference, buffer);
    }

    [Fact]
    public void SystemRandomSource_FillsTheWholeBuffer()
    {
        byte[] first = new byte[64];
        byte[] second = new byte[64];

        SystemRandomSource.Instance.Fill(first);
        SystemRandomSource.Instance.Fill(second);

        Assert.NotEqual(first, second);
        Assert.Contains(first, b => b != 0);
    }
}
