using Bastion.Core.Format;

namespace Bastion.Core.Tests.Vault;

/// <summary>
/// The length half of the tamper matrix of FORMAT.md section 10: the file is cut at every structural
/// boundary and grown past its end. The length equation of section 1 makes all of these
/// <see cref="VaultErrorCode.Truncated"/>, whether the cheap pre-KDF check or the exact check after the
/// index has been decrypted catches them.
/// </summary>
public sealed class TruncationTamperTests
{
    /// <summary>A structural boundary of the file layout of FORMAT.md section 1.</summary>
    public enum Boundary
    {
        /// <summary>Nothing at all.</summary>
        Empty,

        /// <summary>A single byte, less than the magic.</summary>
        OneByte,

        /// <summary>One byte short of a complete header.</summary>
        HeaderMinusOne,

        /// <summary>Exactly the header.</summary>
        HeaderOnly,

        /// <summary>The header plus all but one byte of the primary index.</summary>
        IndexMinusOne,

        /// <summary>The header plus the complete primary index.</summary>
        HeaderAndIndex,

        /// <summary>The first byte of the data section.</summary>
        FirstDataByte,

        /// <summary>All but the last byte of the data section.</summary>
        DataMinusOne,

        /// <summary>Everything but the index copy.</summary>
        WithoutCopy,

        /// <summary>Everything but the last byte of the index copy.</summary>
        CopyMinusOne,
    }

    [Theory]
    [InlineData(Boundary.Empty)]
    [InlineData(Boundary.OneByte)]
    [InlineData(Boundary.HeaderMinusOne)]
    [InlineData(Boundary.HeaderOnly)]
    [InlineData(Boundary.IndexMinusOne)]
    [InlineData(Boundary.HeaderAndIndex)]
    [InlineData(Boundary.FirstDataByte)]
    [InlineData(Boundary.DataMinusOne)]
    [InlineData(Boundary.WithoutCopy)]
    [InlineData(Boundary.CopyMinusOne)]
    public async Task Cutting_the_file_at_a_structural_boundary_reports_a_truncated_vault(Boundary boundary)
    {
        // The multi-chunk file makes the data section larger than one index copy, so the boundaries
        // really are distinct and both the cheap and the exact length check get exercised.
        using TamperVault vault = await TamperVault.CreateAsync();

        long indexLength;
        long dataSectionLength;
        using (VaultImage image = vault.Image())
        {
            indexLength = image.Header.IndexLength;
            dataSectionLength = image.Index.DataSectionLength;
        }

        long full = vault.Original.LongLength;
        long dataOffset = VaultHeader.Size + indexLength;
        long length = boundary switch
        {
            Boundary.Empty => 0,
            Boundary.OneByte => 1,
            Boundary.HeaderMinusOne => VaultHeader.Size - 1,
            Boundary.HeaderOnly => VaultHeader.Size,
            Boundary.IndexMinusOne => dataOffset - 1,
            Boundary.HeaderAndIndex => dataOffset,
            Boundary.FirstDataByte => dataOffset + 1,
            Boundary.DataMinusOne => dataOffset + dataSectionLength - 1,
            Boundary.WithoutCopy => dataOffset + dataSectionLength,
            _ => full - 1,
        };

        Assert.True(length < full, $"{boundary} must really shorten the file");
        byte[] mutated = vault.Original.AsSpan(0, (int)length).ToArray();

        string because = $"the file was cut at {boundary} ({length} of {full} bytes)";
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);

        VaultAssert.Failure(error, VaultErrorCode.Truncated, because);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(65536)]
    public async Task Appending_bytes_to_a_healthy_vault_reports_a_truncated_vault(int extra)
    {
        // Trailing bytes are rejected by the same equation that catches a short file (section 1).
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = new byte[vault.Original.Length + extra];
        vault.Original.CopyTo(mutated, 0);
        for (int i = 0; i < extra; i++)
        {
            mutated[vault.Original.Length + i] = (byte)(0xA5 + i);
        }

        string because = $"{extra} bytes were appended to the file";
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);

        VaultAssert.Failure(error, VaultErrorCode.Truncated, because);
    }

    [Fact]
    public async Task Inserting_bytes_between_the_index_and_the_data_section_reports_a_truncated_vault()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        long dataOffset;
        using (VaultImage image = vault.Image())
        {
            dataOffset = image.DataSectionOffset;
        }

        var mutated = new List<byte>(vault.Original.Length + 32);
        mutated.AddRange(vault.Original.AsSpan(0, (int)dataOffset).ToArray());
        mutated.AddRange(new byte[32]);
        mutated.AddRange(vault.Original.AsSpan((int)dataOffset).ToArray());

        VaultException error = await vault.ExpectOpenFailsAsync(
            [.. mutated], "32 bytes were inserted in front of the data section");

        VaultAssert.Failure(error, VaultErrorCode.Truncated, "bytes inserted before the data section");
    }

    [Fact]
    public async Task A_file_that_is_not_a_vault_is_recognised_before_anything_else()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        new DeterministicRandomSource(4242).Fill(mutated.AsSpan(0, VaultHeader.Size));

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "the header was replaced by random bytes");

        VaultAssert.Failure(error, VaultErrorCode.NotAVault, "random header bytes");
    }

    [Fact]
    public async Task A_short_file_that_starts_with_the_magic_is_still_truncated()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = new byte[VaultHeader.Size / 2];
        VaultHeader.Magic.CopyTo(mutated);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "only half a header was left");

        VaultAssert.Failure(error, VaultErrorCode.Truncated, "half a header");
    }
}
