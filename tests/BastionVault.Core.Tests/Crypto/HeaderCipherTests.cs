using BastionVault.Core.Crypto;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>FORMAT.md sections 2.5 and 4.1: the key wrap and the encrypted index.</summary>
public sealed class HeaderCipherTests
{
    private static byte[] Kek => Filled(32, 0x11);

    private static byte[] WrapNonce => Filled(12, 0x22);

    private static byte[] VaultKey => Filled(32, 0x33);

    private static byte[] WrapAad => Filled(175, 0x44);

    [Fact]
    public void WrapVaultKey_RoundTrips()
    {
        byte[] wrapped = new byte[48];
        HeaderCipher.WrapVaultKey(Kek, WrapNonce, VaultKey, WrapAad, wrapped);

        using KeyMaterial recovered = HeaderCipher.UnwrapVaultKey(Kek, WrapNonce, wrapped, WrapAad);

        Assert.Equal(VaultKey, recovered.Span.ToArray());
        Assert.NotEqual(VaultKey, wrapped[..32]);
    }

    [Fact]
    public void UnwrapVaultKey_WithTheWrongKek_ReportsAuthenticationFailed()
    {
        byte[] wrapped = new byte[48];
        HeaderCipher.WrapVaultKey(Kek, WrapNonce, VaultKey, WrapAad, wrapped);

        byte[] wrongKek = Kek;
        wrongKek[0] ^= 0x01;

        VaultAuthenticationException error = Assert.Throws<VaultAuthenticationException>(
            () => HeaderCipher.UnwrapVaultKey(wrongKek, WrapNonce, wrapped, WrapAad));

        Assert.Equal(VaultErrorCode.AuthenticationFailed, error.Code);
        Assert.Equal("wrong password or keyfile, or the vault header has been altered", error.Message);
    }

    /// <summary>Every input the wrap binds must be able to break it, and always in the same way.</summary>
    [Theory]
    [InlineData("nonce")]
    [InlineData("aad")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    public void UnwrapVaultKey_RejectsEveryTamperedInput(string what)
    {
        byte[] wrapped = new byte[48];
        HeaderCipher.WrapVaultKey(Kek, WrapNonce, VaultKey, WrapAad, wrapped);

        byte[] nonce = WrapNonce;
        byte[] aad = WrapAad;

        switch (what)
        {
            case "nonce":
                nonce[3] ^= 0x01;
                break;
            case "aad":
                aad[100] ^= 0x01;
                break;
            case "ciphertext":
                wrapped[7] ^= 0x01;
                break;
            case "tag":
                wrapped[47] ^= 0x01;
                break;
        }

        VaultAuthenticationException error = Assert.Throws<VaultAuthenticationException>(
            () => HeaderCipher.UnwrapVaultKey(Kek, nonce, wrapped, aad));

        Assert.Equal(VaultErrorCode.AuthenticationFailed, error.Code);
    }

    [Fact]
    public void WrapVaultKey_RejectsWronglySizedSpans()
    {
        Assert.Throws<ArgumentException>(() => HeaderCipher.WrapVaultKey(new byte[31], WrapNonce, VaultKey, WrapAad, new byte[48]));
        Assert.Throws<ArgumentException>(() => HeaderCipher.WrapVaultKey(Kek, new byte[11], VaultKey, WrapAad, new byte[48]));
        Assert.Throws<ArgumentException>(() => HeaderCipher.WrapVaultKey(Kek, WrapNonce, new byte[33], WrapAad, new byte[48]));
        Assert.Throws<ArgumentException>(() => HeaderCipher.WrapVaultKey(Kek, WrapNonce, VaultKey, WrapAad, new byte[47]));
    }

    [Fact]
    public void EncryptIndex_RoundTrips()
    {
        byte[] indexKey = Filled(32, 0x55);
        byte[] nonce = Filled(12, 0x66);
        byte[] aad = Filled(160, 0x77);
        byte[] plaintext = new byte[65536];
        Random.Shared.NextBytes(plaintext);

        byte[] ciphertext = HeaderCipher.EncryptIndex(indexKey, nonce, plaintext, aad);

        Assert.Equal(plaintext.Length + 16, ciphertext.Length);
        Assert.Equal(plaintext, HeaderCipher.DecryptIndex(indexKey, nonce, ciphertext, aad));
    }

    /// <summary>The index and its copy share the plaintext and the AAD but never the nonce.</summary>
    [Fact]
    public void EncryptIndex_WithTwoNonces_ProducesTwoDifferentCiphertexts()
    {
        byte[] indexKey = Filled(32, 0x55);
        byte[] aad = Filled(160, 0x77);
        byte[] plaintext = new byte[4096];
        Random.Shared.NextBytes(plaintext);

        byte[] primary = HeaderCipher.EncryptIndex(indexKey, Filled(12, 0x01), plaintext, aad);
        byte[] copy = HeaderCipher.EncryptIndex(indexKey, Filled(12, 0x02), plaintext, aad);

        Assert.NotEqual(primary, copy);
        Assert.Equal(plaintext, HeaderCipher.DecryptIndex(indexKey, Filled(12, 0x01), primary, aad));
        Assert.Equal(plaintext, HeaderCipher.DecryptIndex(indexKey, Filled(12, 0x02), copy, aad));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("nonce")]
    [InlineData("aad")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    public void DecryptIndex_ReportsIndexCorruptForEveryTamperedInput(string what)
    {
        byte[] indexKey = Filled(32, 0x55);
        byte[] nonce = Filled(12, 0x66);
        byte[] aad = Filled(160, 0x77);
        byte[] plaintext = new byte[1024];
        Random.Shared.NextBytes(plaintext);

        byte[] ciphertext = HeaderCipher.EncryptIndex(indexKey, nonce, plaintext, aad);

        switch (what)
        {
            case "key":
                indexKey[0] ^= 0x01;
                break;
            case "nonce":
                nonce[0] ^= 0x01;
                break;
            case "aad":
                aad[0] ^= 0x01;
                break;
            case "ciphertext":
                ciphertext[0] ^= 0x01;
                break;
            case "tag":
                ciphertext[^1] ^= 0x01;
                break;
        }

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => HeaderCipher.DecryptIndex(indexKey, nonce, ciphertext, aad));

        Assert.Equal(VaultErrorCode.IndexCorrupt, error.Code);
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }
}
