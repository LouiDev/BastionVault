using BastionVault.Core.Crypto;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>FORMAT.md section 2.2: the keyfile digest and the 1 byte .. 1 MiB length rule.</summary>
public sealed class KeyFileTests
{
    /// <summary>Frozen vector, identical to the one asserted directly on <see cref="VaultKeys"/>.</summary>
    [Fact]
    public void FromBytes_ProducesTheFrozenDigest()
    {
        byte[] content = new byte[32];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)i;
        }

        using KeyFile keyFile = KeyFile.FromBytes(content);

        Assert.Equal(
            "af2648569f61f25525852175858cdbccb27a5280426a7001506261a428ca3b1a",
            Convert.ToHexStringLower(keyFile.Digest));
        Assert.Null(keyFile.SourcePath);
    }

    [Fact]
    public void FromBytes_AcceptsTheBoundaryLengths()
    {
        using KeyFile smallest = KeyFile.FromBytes([0x42]);
        using KeyFile largest = KeyFile.FromBytes(new byte[1024 * 1024]);

        Assert.Equal(32, smallest.Digest.Length);
        Assert.Equal(32, largest.Digest.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData((1024 * 1024) + 1)]
    public void FromBytes_RejectsAnOutOfRangeLength(int length)
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => KeyFile.FromBytes(new byte[length]));

        Assert.Equal(VaultErrorCode.UnsupportedParameters, error.Code);
    }

    [Fact]
    public void Load_ReadsTheFileAndRemembersItsPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bastion-keyfile-{Guid.NewGuid():N}.bin");
        byte[] content = new byte[32];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)i;
        }

        File.WriteAllBytes(path, content);
        try
        {
            using KeyFile keyFile = KeyFile.Load(path);

            Assert.Equal(
                "af2648569f61f25525852175858cdbccb27a5280426a7001506261a428ca3b1a",
                Convert.ToHexStringLower(keyFile.Digest));
            Assert.Equal(Path.GetFullPath(path), keyFile.SourcePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RejectsAnEmptyFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bastion-keyfile-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, []);
        try
        {
            VaultFormatException error = Assert.Throws<VaultFormatException>(() => KeyFile.Load(path));

            Assert.Equal(VaultErrorCode.UnsupportedParameters, error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReportsAMissingFileAsAnIoError()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bastion-missing-{Guid.NewGuid():N}.bin");

        VaultIoException error = Assert.Throws<VaultIoException>(() => KeyFile.Load(path));

        Assert.Equal(VaultErrorCode.IoError, error.Code);
        Assert.Equal(path, error.OffendingPath);
    }

    [Fact]
    public void Load_RejectsABlankPath()
    {
        Assert.Throws<ArgumentNullException>(() => KeyFile.Load(null!));
        Assert.Throws<ArgumentException>(() => KeyFile.Load("   "));
    }

    [Fact]
    public void GenerateContent_ProducesTheRequestedNumberOfRandomBytes()
    {
        byte[] first = KeyFile.GenerateContent();
        byte[] second = KeyFile.GenerateContent();

        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GenerateContent_UsesTheSuppliedRandomSource()
    {
        byte[] first = KeyFile.GenerateContent(48, new DeterministicRandomSource(7));
        byte[] second = KeyFile.GenerateContent(48, new DeterministicRandomSource(7));

        Assert.Equal(48, first.Length);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((1024 * 1024) + 1)]
    public void GenerateContent_RejectsAnOutOfRangeLength(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyFile.GenerateContent(length));
    }

    /// <summary>Two different keyfiles must produce two different digests.</summary>
    [Fact]
    public void Digest_DependsOnEveryContentByte()
    {
        byte[] content = new byte[64];
        Random.Shared.NextBytes(content);
        byte[] altered = (byte[])content.Clone();
        altered[33] ^= 0x01;

        using KeyFile first = KeyFile.FromBytes(content);
        using KeyFile second = KeyFile.FromBytes(altered);

        Assert.NotEqual(first.Digest.ToArray(), second.Digest.ToArray());
    }
}
