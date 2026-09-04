using System.Text;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>FORMAT.md section 2.1: <c>pw = UTF8(NFC(passwordString))</c>, 1 .. 1024 bytes, no unpaired surrogates.</summary>
public sealed class PassphraseTests
{
    /// <summary>The decomposed and the composed spelling of the same text must derive the same key.</summary>
    [Fact]
    public void FromString_NormalisesToNfc()
    {
        const string composed = "café";       // U+00E9, the precomposed form (NFC)
        const string decomposed = "café";     // "e" plus U+0301 combining acute (NFD)

        // Guard against an editor normalising this source file: the two literals must really be
        // the NFC and the NFD spelling of the same text.
        Assert.Equal(4, composed.Length);
        Assert.Equal(5, decomposed.Length);
        Assert.NotEqual(composed, decomposed, StringComparer.Ordinal);

        using Passphrase fromComposed = Passphrase.FromString(composed);
        using Passphrase fromDecomposed = Passphrase.FromString(decomposed);

        Assert.Equal(fromComposed.Bytes.ToArray(), fromDecomposed.Bytes.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(composed), fromComposed.Bytes.ToArray());
    }

    /// <summary>The char overload takes the same normalisation path for non-ASCII input.</summary>
    [Fact]
    public void FromChars_NormalisesToNfc()
    {
        using Passphrase fromComposed = Passphrase.FromChars("café");
        using Passphrase fromDecomposed = Passphrase.FromChars("café");

        Assert.Equal(fromComposed.Bytes.ToArray(), fromDecomposed.Bytes.ToArray());
    }

    /// <summary>ASCII takes the fast path but must produce exactly the same bytes as the string overload.</summary>
    [Fact]
    public void FromChars_AsciiFastPath_MatchesFromString()
    {
        const string password = "correct horse battery staple 42!";

        using Passphrase fromChars = Passphrase.FromChars(password);
        using Passphrase fromString = Passphrase.FromString(password);

        Assert.Equal(fromString.Bytes.ToArray(), fromChars.Bytes.ToArray());
        Assert.Equal(password.Length, fromChars.Length);
    }

    /// <summary>No NUL terminator, no trimming, no case folding.</summary>
    [Fact]
    public void FromString_KeepsSurroundingWhitespaceAndCase()
    {
        using Passphrase passphrase = Passphrase.FromString("  MiXeD  ");

        Assert.Equal("  MiXeD  "u8.ToArray(), passphrase.Bytes.ToArray());
    }

    [Fact]
    public void FromString_RejectsNull() => Assert.Throws<ArgumentNullException>(() => Passphrase.FromString(null!));

    [Fact]
    public void FromString_RejectsEmpty() => Assert.Throws<ArgumentException>(() => Passphrase.FromString(string.Empty));

    [Fact]
    public void FromChars_RejectsEmpty() => Assert.Throws<ArgumentException>(() => Passphrase.FromChars(ReadOnlySpan<char>.Empty));

    /// <summary>
    /// An unpaired surrogate is rejected before normalisation, per FORMAT.md section 2.1. The inputs are
    /// built in code rather than through <c>InlineData</c>, because a lone surrogate cannot survive the
    /// UTF-8 encoding of a custom-attribute argument in metadata.
    /// </summary>
    [Fact]
    public void FromString_RejectsUnpairedSurrogates()
    {
        const char high = '\ud83d';
        const char low = '\ude00';

        string[] malformed =
        [
            "a" + high + "z",       // lone high surrogate in the middle
            "a" + low + "z",        // lone low surrogate in the middle
            "tail" + high,          // high surrogate at the very end
            low + "lead",           // low surrogate at the very start
            string.Empty + high,    // nothing but a high surrogate
            "" + low + high,        // a reversed pair
        ];

        foreach (string password in malformed)
        {
            Assert.Throws<ArgumentException>(() => Passphrase.FromString(password));
            Assert.Throws<ArgumentException>(() => Passphrase.FromChars(password.AsSpan()));
        }
    }

    /// <summary>A well-formed surrogate pair is accepted and encoded as four UTF-8 bytes.</summary>
    [Fact]
    public void FromString_AcceptsSurrogatePairs()
    {
        using Passphrase passphrase = Passphrase.FromString("key\U0001F600");

        Assert.Equal(Encoding.UTF8.GetBytes("key\U0001F600"), passphrase.Bytes.ToArray());
    }

    [Fact]
    public void FromString_AcceptsExactlyTheLimit()
    {
        using Passphrase passphrase = Passphrase.FromString(new string('a', 1024));

        Assert.Equal(1024, passphrase.Length);
    }

    [Fact]
    public void FromString_RejectsMoreThanTheLimit()
    {
        Assert.Throws<ArgumentException>(() => Passphrase.FromString(new string('a', 1025)));

        // 513 two-byte characters are 1026 UTF-8 bytes even though the string is shorter than 1024 chars.
        Assert.Throws<ArgumentException>(() => Passphrase.FromString(new string('é', 513)));
    }

    [Fact]
    public void FromChars_RejectsMoreThanTheLimit()
    {
        Assert.Throws<ArgumentException>(() => Passphrase.FromChars(new string('a', 1025).AsSpan()));
    }
}
