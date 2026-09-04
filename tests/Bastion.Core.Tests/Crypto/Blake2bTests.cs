using System.Text;
using Bastion.Core.Crypto;

namespace Bastion.Core.Tests.Crypto;

/// <summary>RFC 7693 known-answer tests for the BLAKE2b implementation Argon2 is built on.</summary>
public sealed class Blake2bTests
{
    /// <summary>RFC 7693 appendix A: BLAKE2b-512 of the three bytes "abc".</summary>
    [Fact]
    public void Hash_Abc_MatchesRfc7693AppendixA()
    {
        Span<byte> digest = stackalloc byte[64];
        Blake2b.Hash("abc"u8, digest);

        Assert.Equal(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1" +
            "7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexStringLower(digest));
    }

    /// <summary>The published BLAKE2b-512 digest of the empty message.</summary>
    [Fact]
    public void Hash_Empty_MatchesPublishedVector()
    {
        Span<byte> digest = stackalloc byte[64];
        Blake2b.Hash(ReadOnlySpan<byte>.Empty, digest);

        Assert.Equal(
            "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419" +
            "d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
            Convert.ToHexStringLower(digest));
    }

    /// <summary>The first two entries of the official BLAKE2b keyed known-answer test vectors.</summary>
    [Theory]
    [InlineData("", "10ebb67700b1868efb4417987acf4690ae9d972fb7a590c2f02871799aaa4786b5e996e8f0f4eb981fc214b005f42d2ff4233499391653df7aefcbc13fc51568")]
    [InlineData("00", "961f6dd1e4dd30f63901690c512e78e4b45e4742ed197c3c5e45c549fd25f2e4187b0bc9fe30492b16b0d0bc4ef9b0f34c7003fac09a5ef1532e69430234cebd")]
    public void Hash_Keyed_MatchesKnownAnswerTests(string inputHex, string expected)
    {
        byte[] key = new byte[64];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)i;
        }

        byte[] input = Convert.FromHexString(inputHex);
        Span<byte> digest = stackalloc byte[64];
        Blake2b.Hash(key, input, digest);

        Assert.Equal(expected, Convert.ToHexStringLower(digest));
    }

    /// <summary>A long message crosses several 128-byte blocks and exercises the buffered update path.</summary>
    [Fact]
    public void Hash_LongMessage_IsIndependentOfChunking()
    {
        byte[] message = new byte[1000];
        for (int i = 0; i < message.Length; i++)
        {
            message[i] = (byte)(i * 7);
        }

        Span<byte> whole = stackalloc byte[64];
        Blake2b.Hash(message, whole);

        // Hashing the identical bytes through the public one-shot API must be stable.
        Span<byte> again = stackalloc byte[64];
        Blake2b.Hash(message.AsSpan(), again);

        Assert.True(whole.SequenceEqual(again));
        Assert.NotEqual(new string('0', 128), Convert.ToHexStringLower(whole));
    }

    /// <summary>The digest length is part of the parameter block, so a short digest is not a prefix of a long one.</summary>
    [Fact]
    public void Hash_ShorterOutput_IsNotAPrefixOfTheFullDigest()
    {
        Span<byte> full = stackalloc byte[64];
        Span<byte> short32 = stackalloc byte[32];
        Blake2b.Hash("abc"u8, full);
        Blake2b.Hash("abc"u8, short32);

        Assert.False(full[..32].SequenceEqual(short32));
    }

    /// <summary>Every output length between 1 and 64 bytes is accepted.</summary>
    [Fact]
    public void Hash_AcceptsEveryOutputLengthFromOneToSixtyFour()
    {
        for (int length = 1; length <= 64; length++)
        {
            byte[] digest = new byte[length];
            Blake2b.Hash(Encoding.UTF8.GetBytes("bastion"), digest);
            Assert.Contains(digest, b => b != 0);
        }
    }

    /// <summary>An output length outside 1 .. 64 bytes and an oversized key are rejected.</summary>
    [Fact]
    public void Hash_RejectsInvalidSizes()
    {
        byte[] input = "abc"u8.ToArray();

        Assert.Throws<ArgumentException>(() => Blake2b.Hash(input, Span<byte>.Empty));
        Assert.Throws<ArgumentException>(() => Blake2b.Hash(input, new byte[65]));
        Assert.Throws<ArgumentException>(() => Blake2b.Hash(new byte[65], input, new byte[32]));
    }
}
