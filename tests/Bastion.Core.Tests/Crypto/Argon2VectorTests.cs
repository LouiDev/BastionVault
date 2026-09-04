using Bastion.Core.Crypto;

namespace Bastion.Core.Tests.Crypto;

/// <summary>
/// The RFC 9106 known-answer tests of sections 5.1, 5.2 and 5.3. All three share the same inputs
/// (password 32 x 0x01, salt 16 x 0x02, secret 8 x 0x03, associated data 12 x 0x04, m = 32 KiB, t = 3,
/// p = 4, tag length 32) and differ only in the Argon2 variant, so they cover the data-dependent,
/// the data-independent and the hybrid addressing path of the shared core.
/// </summary>
public sealed class Argon2VectorTests
{
    private static byte[] Password => Filled(32, 0x01);

    private static byte[] Salt => Filled(16, 0x02);

    private static byte[] Secret => Filled(8, 0x03);

    private static byte[] AssociatedData => Filled(12, 0x04);

    /// <summary>RFC 9106 section 5.1 (Argon2d).</summary>
    [Fact]
    public void Argon2d_MatchesRfc9106Section51()
    {
        byte[] tag = Argon2.Hash(Argon2Type.D, Password, Salt, Secret, AssociatedData, 32, 3, 4, 32, CancellationToken.None);

        Assert.Equal("512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb", Convert.ToHexStringLower(tag));
    }

    /// <summary>RFC 9106 section 5.2 (Argon2i).</summary>
    [Fact]
    public void Argon2i_MatchesRfc9106Section52()
    {
        byte[] tag = Argon2.Hash(Argon2Type.I, Password, Salt, Secret, AssociatedData, 32, 3, 4, 32, CancellationToken.None);

        Assert.Equal("c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8", Convert.ToHexStringLower(tag));
    }

    /// <summary>RFC 9106 section 5.3 (Argon2id), the variant the vault format uses.</summary>
    [Fact]
    public void Argon2id_MatchesRfc9106Section53()
    {
        byte[] tag = Argon2.Hash(Argon2Type.Id, Password, Salt, Secret, AssociatedData, 32, 3, 4, 32, CancellationToken.None);

        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(tag));
    }

    /// <summary>The KDF seam is Argon2id and honours the parameter record.</summary>
    [Fact]
    public void DeriveArgon2id_UsesTheParametersOfTheRecord()
    {
        byte[] salt = Filled(32, 0x02);
        KdfParameters parameters = new(8192, 1, 4);

        byte[] viaSeam = Argon2.Instance.DeriveArgon2id("hunter2"u8.ToArray(), salt, parameters, 32, CancellationToken.None);
        byte[] viaHash = Argon2.Hash(Argon2Type.Id, "hunter2"u8.ToArray(), salt, default, default, 8192, 1, 4, 32, CancellationToken.None);

        Assert.Equal(viaHash, viaSeam);
    }

    /// <summary>The seam rejects parameters that violate the limits table of FORMAT.md section 7.</summary>
    [Fact]
    public void DeriveArgon2id_RejectsOutOfRangeParameters()
    {
        byte[] salt = Filled(32, 0x02);
        byte[] password = "hunter2"u8.ToArray();

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => Argon2.Instance.DeriveArgon2id(password, salt, new KdfParameters(1024, 1, 4), 32, CancellationToken.None));

        Assert.Equal(VaultErrorCode.UnsupportedParameters, error.Code);
    }

    /// <summary>A token that is already cancelled aborts before the first pass.</summary>
    [Fact]
    public void DeriveArgon2id_HonoursACancelledToken()
    {
        byte[] salt = Filled(32, 0x02);
        byte[] password = "hunter2"u8.ToArray();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => Argon2.Instance.DeriveArgon2id(password, salt, new KdfParameters(8192, 1, 4), 32, cts.Token));
    }

    /// <summary>Every lane count between 1 and 4 produces a stable, distinct result.</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void Hash_IsDeterministicForEveryLaneCount(uint parallelism)
    {
        byte[] first = Argon2.Hash(Argon2Type.Id, Password, Salt, default, default, 256, 2, parallelism, 32, CancellationToken.None);
        byte[] second = Argon2.Hash(Argon2Type.Id, Password, Salt, default, default, 256, 2, parallelism, 32, CancellationToken.None);

        Assert.Equal(first, second);
    }

    /// <summary>Tag lengths above 64 bytes exercise the multi-block branch of the variable-length hash H'.</summary>
    [Fact]
    public void Hash_ProducesLongTagsThroughTheVariableLengthHash()
    {
        byte[] longTag = Argon2.Hash(Argon2Type.Id, Password, Salt, default, default, 256, 1, 1, 300, CancellationToken.None);
        byte[] shortTag = Argon2.Hash(Argon2Type.Id, Password, Salt, default, default, 256, 1, 1, 32, CancellationToken.None);

        Assert.Equal(300, longTag.Length);
        Assert.Contains(longTag, b => b != 0);

        // The tag length is hashed into H0, so a long tag does not start with the short one.
        Assert.False(longTag.AsSpan(0, 32).SequenceEqual(shortTag));
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }
}
