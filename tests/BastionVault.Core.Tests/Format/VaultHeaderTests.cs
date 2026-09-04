using System.Buffers.Binary;
using System.Text;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Format;

/// <summary>Covers FORMAT.md section 3 (layout), section 3.1 (validation order) and section 2.6 (AADs).</summary>
public sealed class VaultHeaderTests
{
    /// <summary>
    /// The frozen byte image of <see cref="Reference"/>, written out by hand from the field table of
    /// FORMAT.md section 3. A change to this constant is a format change.
    /// </summary>
    private const string GoldenHeaderHex =
        "894253544E0D0A1A0100A0000000010001010000000008000300000004000000" +
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
        "A0A1A2A3A4A5A6A7A8A9AAAB404142434445464748494A4B4C4D4E4F50515253" +
        "5455565758595A5B5C5D5E5F606162636465666768696A6B6C6D6E6FB0B1B2B3" +
        "B4B5B6B7B8B9BABBC0C1C2C3C4C5C6C7C8C9CACB100001000000000000000000";

    /// <summary>Index length of the reference header: the smallest legal value (64 KiB plus a tag).</summary>
    private const long ReferenceIndexLength = 65552;

    /// <summary>Smallest file length the reference header accepts (header plus both index copies).</summary>
    private const long ReferenceFileLength = VaultHeader.Size + (2 * ReferenceIndexLength);

    [Fact]
    public void Write_ProducesTheGoldenImage()
    {
        byte[] buffer = new byte[VaultHeader.Size];
        Reference().Write(buffer);

        Assert.Equal(GoldenHeaderHex, Convert.ToHexString(buffer));
    }

    [Fact]
    public void Write_RejectsAShortDestination()
    {
        Assert.Throws<ArgumentException>(() => Reference().Write(new byte[VaultHeader.Size - 1]));
    }

    [Fact]
    public void Write_RejectsAFieldOfTheWrongSize()
    {
        VaultHeader header = Reference(salt: new byte[31]);

        Assert.Throws<InvalidOperationException>(() => header.Write(new byte[VaultHeader.Size]));
    }

    [Fact]
    public void ParseOfTheGoldenImage_ReturnsEveryField()
    {
        VaultHeader header = VaultHeader.Parse(Golden(), ReferenceFileLength);

        Assert.Equal(1, header.FormatVersion);
        Assert.Equal(0x0001_0000u, header.Flags);
        Assert.Equal(new KdfParameters(524288, 3, 4), header.Kdf);
        Assert.Equal(Sequence(0x00, 32), header.KdfSalt);
        Assert.Equal(Sequence(0xA0, 12), header.WrapNonce);
        Assert.Equal(Sequence(0x40, 48), header.WrappedVaultKey);
        Assert.Equal(Sequence(0xB0, 12), header.IndexNonce);
        Assert.Equal(Sequence(0xC0, 12), header.IndexCopyNonce);
        Assert.Equal(ReferenceIndexLength, header.IndexLength);
        Assert.Equal(VaultHeader.Size + ReferenceIndexLength, header.DataSectionOffset);
    }

    [Fact]
    public void WriteThenParse_RoundTrips()
    {
        VaultHeader original = Reference(flags: 0, kdf: new KdfParameters(65536, 3, 4), indexLength: 1024 * 1024);

        byte[] buffer = new byte[VaultHeader.Size];
        original.Write(buffer);
        VaultHeader parsed = VaultHeader.Parse(buffer, VaultHeader.Size + (2 * original.IndexLength));

        byte[] again = new byte[VaultHeader.Size];
        parsed.Write(again);
        Assert.Equal(buffer, again);
        Assert.Equal(original.Flags, parsed.Flags);
        Assert.Equal(original.Kdf, parsed.Kdf);
        Assert.Equal(original.IndexLength, parsed.IndexLength);
    }

    [Fact]
    public void Parse_AcceptsExtraBytesAfterTheHeader()
    {
        byte[] withTrailer = [.. Golden(), .. new byte[64]];

        VaultHeader header = VaultHeader.Parse(withTrailer, ReferenceFileLength);

        Assert.Equal(ReferenceIndexLength, header.IndexLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(159)]
    public void Parse_RejectsAFileShorterThanTheHeader(long fileLength)
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => VaultHeader.Parse(Golden(), fileLength));

        Assert.Equal(VaultErrorCode.Truncated, error.Code);
    }

    [Fact]
    public void Parse_RejectsFewerThan160Bytes()
    {
        byte[] short159 = Golden()[..159];

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(short159, ReferenceFileLength));

        Assert.Equal(VaultErrorCode.Truncated, error.Code);
    }

    [Fact]
    public void Parse_RejectsAFileThatCannotHoldBothIndexCopies()
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(Golden(), ReferenceFileLength - 1));

        Assert.Equal(VaultErrorCode.Truncated, error.Code);
    }

    [Fact]
    public void Parse_RejectsEqualIndexNonces()
    {
        byte[] bytes = Golden();
        bytes.AsSpan(124, 12).CopyTo(bytes.AsSpan(136));

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(bytes, ReferenceFileLength));

        Assert.Equal(VaultErrorCode.HeaderCorrupt, error.Code);
    }

    [Fact]
    public void Parse_RejectsTheReservedChaChaCipherId()
    {
        byte[] bytes = Golden();
        bytes[17] = 2;

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(bytes, ReferenceFileLength));

        Assert.Equal(VaultErrorCode.UnsupportedParameters, error.Code);
    }

    [Theory]
    [InlineData(2, VaultErrorCode.UnsupportedVersion)]
    [InlineData(0, VaultErrorCode.HeaderCorrupt)]
    [InlineData(65535, VaultErrorCode.UnsupportedVersion)]
    public void Parse_ChecksTheFormatVersion(int version, VaultErrorCode expected)
    {
        byte[] bytes = Golden();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)version);

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(bytes, ReferenceFileLength));

        Assert.Equal(expected, error.Code);
    }

    [Theory]
    [InlineData(65551UL)]                      // one below the minimum
    [InlineData((64UL * 1024 * 1024) + 17)]    // one above the maximum
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void Parse_ChecksTheIndexLengthRange(ulong indexLength)
    {
        byte[] bytes = Golden();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(148), indexLength);

        VaultFormatException error = Assert.Throws<VaultFormatException>(
            () => VaultHeader.Parse(bytes, long.MaxValue));

        Assert.Equal(VaultErrorCode.HeaderCorrupt, error.Code);
    }

    [Fact]
    public void Parse_AcceptsAdvisoryFlagsAndKeepsThem()
    {
        byte[] bytes = Golden();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0xABCD_0000u);

        VaultHeader header = VaultHeader.Parse(bytes, ReferenceFileLength);

        Assert.Equal(0xABCD_0000u, header.Flags);
    }

    /// <summary>
    /// Flips every single bit of the header and asserts the exact outcome demanded by FORMAT.md
    /// section 3.1 — including the bits that must not change the verdict at all.
    /// </summary>
    [Fact]
    public void Parse_ReactsToEverySingleBitFlipExactlyAsSpecified()
    {
        for (int offset = 0; offset < VaultHeader.Size; offset++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] bytes = Golden();
                bytes[offset] ^= (byte)(1 << bit);

                VaultErrorCode? expected = ExpectedVerdict(offset, bytes);
                string where = $"offset {offset}, bit {bit}";

                if (expected is null)
                {
                    VaultHeader parsed = VaultHeader.Parse(bytes, ReferenceFileLength);
                    Assert.True(parsed.FormatVersion == 1, where);
                    continue;
                }

                VaultFormatException error = Assert.Throws<VaultFormatException>(
                    () => VaultHeader.Parse(bytes, ReferenceFileLength));
                Assert.True(expected == error.Code, $"{where}: expected {expected}, got {error.Code} ({error.Message})");
            }
        }
    }

    [Fact]
    public void BuildWrapAad_MatchesAHandBuiltExpectation()
    {
        byte[] expected = new byte[15 + VaultHeader.Size];
        Encoding.ASCII.GetBytes("bastion/v1/wrap").CopyTo(expected, 0);
        Golden().CopyTo(expected, 15);
        Array.Clear(expected, 15 + 76, 156 - 76);

        byte[] actual = Reference().BuildWrapAad();

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        Assert.Equal(175, actual.Length);
    }

    [Fact]
    public void BuildIndexAad_MatchesAHandBuiltExpectation()
    {
        byte[] expected = new byte[16 + VaultHeader.Size];
        Encoding.ASCII.GetBytes("bastion/v1/index").CopyTo(expected, 0);
        Golden().CopyTo(expected, 16);
        Array.Clear(expected, 16 + 124, 148 - 124);

        byte[] actual = Reference().BuildIndexAad();

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        Assert.Equal(176, actual.Length);
    }

    [Fact]
    public void WrapAad_IgnoresTheWrappedKeyTheNoncesAndTheIndexLength()
    {
        VaultHeader changed = Reference(
            wrapped: Sequence(0x01, 48),
            indexNonce: Sequence(0x11, 12),
            indexCopyNonce: Sequence(0x22, 12),
            indexLength: 4 * 1024 * 1024);

        Assert.Equal(Reference().BuildWrapAad(), changed.BuildWrapAad());
    }

    [Fact]
    public void IndexAad_CoversTheWrappedKeyAndTheIndexLengthButNotTheNonces()
    {
        byte[] baseline = Reference().BuildIndexAad();

        Assert.Equal(baseline, Reference(indexNonce: Sequence(0x11, 12)).BuildIndexAad());
        Assert.Equal(baseline, Reference(indexCopyNonce: Sequence(0x22, 12)).BuildIndexAad());
        Assert.NotEqual(baseline, Reference(wrapped: Sequence(0x01, 48)).BuildIndexAad());
        Assert.NotEqual(baseline, Reference(indexLength: 131088).BuildIndexAad());
        Assert.NotEqual(baseline, Reference(salt: Sequence(0x77, 32)).BuildIndexAad());
    }

    /// <summary>
    /// The verdict FORMAT.md section 3.1 demands for a header that differs from the reference in the
    /// given byte, or <see langword="null"/> when the change must still parse.
    /// </summary>
    /// <param name="offset">Offset of the changed byte.</param>
    /// <param name="bytes">The mutated header.</param>
    private static VaultErrorCode? ExpectedVerdict(int offset, byte[] bytes) => offset switch
    {
        < 8 => VaultErrorCode.NotAVault,
        < 10 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) == 0
            ? VaultErrorCode.HeaderCorrupt
            : VaultErrorCode.UnsupportedVersion,
        < 12 => VaultErrorCode.HeaderCorrupt,               // headerLength
        < 14 => VaultErrorCode.UnsupportedParameters,       // critical flag bits
        < 16 => null,                                       // advisory flag bits
        < 18 => VaultErrorCode.UnsupportedParameters,       // kdfId, cipherId
        < 20 => VaultErrorCode.HeaderCorrupt,               // reserved0
        < 32 => new KdfParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28))).IsValid
            ? null
            : VaultErrorCode.UnsupportedParameters,
        < 148 => null,                                      // salt, nonces, wrapped key
        < 156 => IndexLengthVerdict(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(148))),
        _ => VaultErrorCode.HeaderCorrupt,                  // reserved1
    };

    /// <summary>Verdict for a mutated index length against <see cref="ReferenceFileLength"/>.</summary>
    /// <param name="indexLength">The mutated value.</param>
    private static VaultErrorCode? IndexLengthVerdict(ulong indexLength)
    {
        if (indexLength is < 65552 or > (64UL * 1024 * 1024) + 16)
        {
            return VaultErrorCode.HeaderCorrupt;
        }

        return ReferenceFileLength < VaultHeader.Size + (2 * (long)indexLength) ? VaultErrorCode.Truncated : null;
    }

    /// <summary>The header the golden image describes, with individually overridable fields.</summary>
    /// <param name="flags">Feature flags.</param>
    /// <param name="kdf">Argon2id cost parameters.</param>
    /// <param name="salt">32-byte KDF salt.</param>
    /// <param name="wrapNonce">12-byte wrap nonce.</param>
    /// <param name="wrapped">48-byte wrapped vault key.</param>
    /// <param name="indexNonce">12-byte index nonce.</param>
    /// <param name="indexCopyNonce">12-byte index copy nonce.</param>
    /// <param name="indexLength">Encrypted index length.</param>
    private static VaultHeader Reference(
        uint? flags = null,
        KdfParameters? kdf = null,
        byte[]? salt = null,
        byte[]? wrapNonce = null,
        byte[]? wrapped = null,
        byte[]? indexNonce = null,
        byte[]? indexCopyNonce = null,
        long? indexLength = null) => new()
    {
        FormatVersion = 1,
        Flags = flags ?? 0x0001_0000u,
        Kdf = kdf ?? new KdfParameters(524288, 3, 4),
        KdfSalt = salt ?? Sequence(0x00, 32),
        WrapNonce = wrapNonce ?? Sequence(0xA0, 12),
        WrappedVaultKey = wrapped ?? Sequence(0x40, 48),
        IndexNonce = indexNonce ?? Sequence(0xB0, 12),
        IndexCopyNonce = indexCopyNonce ?? Sequence(0xC0, 12),
        IndexLength = indexLength ?? ReferenceIndexLength,
    };

    /// <summary>A fresh copy of the golden header bytes.</summary>
    private static byte[] Golden() => Convert.FromHexString(GoldenHeaderHex);

    /// <summary>Builds <paramref name="count"/> consecutive bytes starting at <paramref name="start"/>.</summary>
    /// <param name="start">First byte value.</param>
    /// <param name="count">Number of bytes.</param>
    private static byte[] Sequence(int start, int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }
}
