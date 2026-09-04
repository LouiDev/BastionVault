using Bastion.Core.Crypto;

namespace Bastion.Core.Tests.Crypto;

/// <summary>FORMAT.md sections 2.2 to 2.4: the keyfile digest, the KEK and the three derived keys.</summary>
public sealed class VaultKeysTests
{
    private static byte[] Argon2Output => Filled(32, 0x10);

    private static byte[] KeyfileDigest => Filled(32, 0x20);

    private static byte[] KdfSalt => Filled(32, 0x30);

    private static byte[] VaultKey => Filled(32, 0x40);

    private static byte[] BlobId => Filled(16, 0x50);

    /// <summary>Frozen vector: <c>HMAC-SHA256("bastion/v1/keyfile", 00 01 .. 1f)</c>.</summary>
    [Fact]
    public void ComputeKeyfileDigest_MatchesTheFrozenVector()
    {
        byte[] content = new byte[32];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)i;
        }

        Assert.Equal(
            "af2648569f61f25525852175858cdbccb27a5280426a7001506261a428ca3b1a",
            Convert.ToHexStringLower(VaultKeys.ComputeKeyfileDigest(content)));
    }

    [Fact]
    public void DeriveKek_IsDeterministic()
    {
        using KeyMaterial first = VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, KdfSalt);
        using KeyMaterial second = VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, KdfSalt);

        Assert.Equal(32, first.Length);
        Assert.Equal(first.Span.ToArray(), second.Span.ToArray());
    }

    /// <summary>
    /// The <c>keyfilePresent</c> byte domain-separates "no keyfile" from "a keyfile", so an attacker cannot
    /// make a keyfile-less vault look like a keyfile vault by supplying an all-zero digest.
    /// </summary>
    [Fact]
    public void DeriveKek_SeparatesTheKeyfilePresentCases()
    {
        using KeyMaterial withoutKeyfile = VaultKeys.DeriveKek(Argon2Output, ReadOnlySpan<byte>.Empty, KdfSalt);
        using KeyMaterial withZeroDigest = VaultKeys.DeriveKek(Argon2Output, new byte[32], KdfSalt);
        using KeyMaterial withKeyfile = VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, KdfSalt);

        Assert.NotEqual(withoutKeyfile.Span.ToArray(), withZeroDigest.Span.ToArray());
        Assert.NotEqual(withoutKeyfile.Span.ToArray(), withKeyfile.Span.ToArray());
        Assert.NotEqual(withZeroDigest.Span.ToArray(), withKeyfile.Span.ToArray());
    }

    [Fact]
    public void DeriveKek_DependsOnTheArgon2OutputAndOnTheSalt()
    {
        byte[] otherArgon2 = Argon2Output;
        otherArgon2[0] ^= 0x01;
        byte[] otherSalt = KdfSalt;
        otherSalt[0] ^= 0x01;

        using KeyMaterial baseline = VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, KdfSalt);
        using KeyMaterial otherOutput = VaultKeys.DeriveKek(otherArgon2, KeyfileDigest, KdfSalt);
        using KeyMaterial otherSaltKek = VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, otherSalt);

        Assert.NotEqual(baseline.Span.ToArray(), otherOutput.Span.ToArray());
        Assert.NotEqual(baseline.Span.ToArray(), otherSaltKek.Span.ToArray());
    }

    [Fact]
    public void DeriveKek_RejectsAMalformedKeyfileDigest()
    {
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveKek(Argon2Output, new byte[31], KdfSalt));
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveKek(ReadOnlySpan<byte>.Empty, KeyfileDigest, KdfSalt));
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveKek(Argon2Output, KeyfileDigest, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DerivedKeys_AreDeterministicAndHaveTheDocumentedLengths()
    {
        byte[] vaultId = VaultKeys.DeriveVaultId(VaultKey);
        using KeyMaterial indexKey = VaultKeys.DeriveIndexKey(VaultKey);
        using KeyMaterial blobKey = VaultKeys.DeriveBlobKey(VaultKey, BlobId);

        Assert.Equal(16, vaultId.Length);
        Assert.Equal(32, indexKey.Length);
        Assert.Equal(32, blobKey.Length);

        Assert.Equal(vaultId, VaultKeys.DeriveVaultId(VaultKey));
        using KeyMaterial indexKeyAgain = VaultKeys.DeriveIndexKey(VaultKey);
        using KeyMaterial blobKeyAgain = VaultKeys.DeriveBlobKey(VaultKey, BlobId);
        Assert.Equal(indexKey.Span.ToArray(), indexKeyAgain.Span.ToArray());
        Assert.Equal(blobKey.Span.ToArray(), blobKeyAgain.Span.ToArray());
    }

    /// <summary>The three labels domain-separate the derived keys from each other and from the vault key.</summary>
    [Fact]
    public void DerivedKeys_AreDistinctFromEachOther()
    {
        byte[] vaultId = VaultKeys.DeriveVaultId(VaultKey);
        using KeyMaterial indexKey = VaultKeys.DeriveIndexKey(VaultKey);
        using KeyMaterial blobKey = VaultKeys.DeriveBlobKey(VaultKey, BlobId);

        Assert.NotEqual(indexKey.Span.ToArray(), blobKey.Span.ToArray());
        Assert.NotEqual(VaultKey, indexKey.Span.ToArray());
        Assert.NotEqual(VaultKey, blobKey.Span.ToArray());
        Assert.NotEqual(vaultId, indexKey.Span[..16].ToArray());
        Assert.NotEqual(vaultId, blobKey.Span[..16].ToArray());
    }

    /// <summary>Each blob id yields its own key; this is what makes the per-chunk nonce reuse impossible.</summary>
    [Fact]
    public void DeriveBlobKey_DependsOnTheBlobId()
    {
        byte[] otherBlobId = BlobId;
        otherBlobId[15] ^= 0x01;

        using KeyMaterial first = VaultKeys.DeriveBlobKey(VaultKey, BlobId);
        using KeyMaterial second = VaultKeys.DeriveBlobKey(VaultKey, otherBlobId);

        Assert.NotEqual(first.Span.ToArray(), second.Span.ToArray());
    }

    /// <summary>A different vault key changes every derived key, which is what re-key relies on.</summary>
    [Fact]
    public void DerivedKeys_ChangeWithTheVaultKey()
    {
        byte[] otherVaultKey = VaultKey;
        otherVaultKey[31] ^= 0x01;

        using KeyMaterial indexKey = VaultKeys.DeriveIndexKey(VaultKey);
        using KeyMaterial otherIndexKey = VaultKeys.DeriveIndexKey(otherVaultKey);

        Assert.NotEqual(VaultKeys.DeriveVaultId(VaultKey), VaultKeys.DeriveVaultId(otherVaultKey));
        Assert.NotEqual(indexKey.Span.ToArray(), otherIndexKey.Span.ToArray());
    }

    [Fact]
    public void DerivedKeys_RejectMalformedInput()
    {
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveVaultId(new byte[31]));
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveIndexKey(new byte[33]));
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveBlobKey(new byte[31], BlobId));
        Assert.Throws<ArgumentException>(() => VaultKeys.DeriveBlobKey(VaultKey, new byte[15]));
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }
}
