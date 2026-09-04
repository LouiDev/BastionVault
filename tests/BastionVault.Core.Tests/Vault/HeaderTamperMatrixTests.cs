using System.Buffers.Binary;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The header half of the tamper matrix of FORMAT.md section 10, run end to end through
/// <see cref="VaultFactory.OpenAsync"/> rather than against the parser alone. Every one of the 160
/// header bytes is flipped in turn; each flip must produce exactly the verdict of FORMAT.md section
/// 3.1, and no flip may let a single plaintext byte reach the disk.
/// </summary>
/// <remarks>
/// The parser-level sweep over all 1280 single-bit variants lives in
/// <c>Format/VaultHeaderTests.Parse_ReactsToEverySingleBitFlipExactlyAsSpecified</c>. This class adds
/// what only a real vault can show: which flips are caught by the AEAD rather than by a range check,
/// and which ones the format survives because a second, independent copy of the index exists.
/// </remarks>
public sealed class HeaderTamperMatrixTests
{
    /// <summary>Bit flipped in every byte; bit 6 changes an ASCII digit and a length alike.</summary>
    private const byte FlippedBit = 0x40;

    [Theory]
    [InlineData("magic", 0, 8)]
    [InlineData("formatVersion", 8, 2)]
    [InlineData("headerLength", 10, 2)]
    [InlineData("flags (critical bits 0..15)", 12, 2)]
    [InlineData("flags (advisory bits 16..31)", 14, 2)]
    [InlineData("kdfId", 16, 1)]
    [InlineData("cipherId", 17, 1)]
    [InlineData("reserved0", 18, 2)]
    [InlineData("kdfMemoryKiB", 20, 4)]
    [InlineData("kdfIterations", 24, 4)]
    [InlineData("kdfParallelism", 28, 4)]
    [InlineData("kdfSalt", 32, 32)]
    [InlineData("wrapNonce", 64, 12)]
    [InlineData("wrappedVaultKey", 76, 48)]
    [InlineData("indexNonce", 124, 12)]
    [InlineData("indexCopyNonce", 136, 12)]
    [InlineData("indexLength", 148, 8)]
    [InlineData("reserved1", 156, 4)]
    public async Task Flipping_a_bit_in_a_header_field_gives_the_verdict_of_section_3_1(string field, int start, int length)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        for (int offset = start; offset < start + length; offset++)
        {
            byte[] mutated = vault.Copy();
            mutated[offset] ^= FlippedBit;

            string because = $"{field}: bit 0x{FlippedBit:X2} of offset {offset} was flipped";
            Verdict verdict = Expect(offset, mutated, mutated.LongLength);

            if (verdict.Code is VaultErrorCode code)
            {
                VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);
                VaultAssert.Failure(error, code, because);
                continue;
            }

            vault.Write(mutated);
            await using IVaultSession session = await vault.OpenTargetAsync();

            Assert.True(
                session.Statistics.OpenedFromIndexCopy == verdict.FromIndexCopy,
                $"{because}: expected OpenedFromIndexCopy={verdict.FromIndexCopy}.");
            Assert.Equal(3, session.GetChildren(EntryId.Root).Count);
            Assert.True(session.TryResolvePath("\\" + TamperVault.MarkerFile, out EntryId marker), because);
            Assert.Equal(
                TamperVault.Marker,
                System.Text.Encoding.ASCII.GetString(await GoldenFixtureTests.ReadAllAsync(session, marker)));
        }
    }

    [Fact]
    public async Task Format_version_zero_is_a_corrupt_header_rather_than_an_unsupported_one()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(8), 0);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "formatVersion was set to 0");
        VaultAssert.Failure(error, VaultErrorCode.HeaderCorrupt, "formatVersion 0");
    }

    [Fact]
    public async Task A_future_format_version_is_refused_before_the_key_derivation()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt16LittleEndian(mutated.AsSpan(8), 2);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "formatVersion was set to 2");
        VaultAssert.Failure(error, VaultErrorCode.UnsupportedVersion, "formatVersion 2");
    }

    [Fact]
    public async Task The_reserved_ChaCha_cipher_id_is_refused()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        mutated[17] = 2;

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "cipherId was set to the reserved value 2");
        VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, "cipherId 2");
        Assert.Contains("ChaCha20-Poly1305", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Equal_index_nonces_are_a_corrupt_header()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        mutated.AsSpan(124, 12).CopyTo(mutated.AsSpan(136));

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "both index nonces were made equal");
        VaultAssert.Failure(error, VaultErrorCode.HeaderCorrupt, "equal index nonces");
    }

    [Fact]
    public async Task Setting_any_critical_flag_bit_is_refused_while_advisory_bits_only_break_the_wrap()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        for (int bit = 0; bit < 16; bit++)
        {
            byte[] mutated = vault.Copy();
            BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(12), 1u << bit);

            VaultException error = await vault.ExpectOpenFailsAsync(mutated, $"critical flag bit {bit} was set");
            VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, $"critical flag bit {bit}");
        }

        for (int bit = 16; bit < 32; bit++)
        {
            byte[] mutated = vault.Copy();
            BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(12), 1u << bit);

            // The parser ignores advisory bits, but wrapAAD covers the whole flags field (section 2.6),
            // so an advisory bit that nobody wrote still fails the key unwrap.
            VaultException error = await vault.ExpectOpenFailsAsync(mutated, $"advisory flag bit {bit} was set");
            VaultAssert.Failure(error, VaultErrorCode.AuthenticationFailed, $"advisory flag bit {bit}");
        }
    }

    [Theory]
    [InlineData(0u, 1u, 1u)]                    // memory below the minimum
    [InlineData(8191u, 1u, 1u)]                 // memory one KiB below the minimum
    [InlineData(4194308u, 1u, 1u)]              // memory above the maximum
    [InlineData(8194u, 1u, 1u)]                 // memory not a multiple of 4 * parallelism
    [InlineData(8192u, 0u, 1u)]                 // zero iterations
    [InlineData(8192u, 65u, 1u)]                // one iteration too many
    [InlineData(8192u, 1u, 0u)]                 // zero lanes
    [InlineData(8192u, 1u, 17u)]                // one lane too many
    [InlineData(8196u, 1u, 4u)]                 // memory not a multiple of 4 * parallelism for four lanes
    public async Task A_kdf_parameter_outside_the_limits_table_is_refused(uint memoryKiB, uint iterations, uint parallelism)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(20), memoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(24), iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(28), parallelism);

        string because = $"kdf m={memoryKiB} t={iterations} p={parallelism}";
        Assert.False(new KdfParameters(memoryKiB, iterations, parallelism).IsValid, because);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);
        VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, because);
    }

    [Fact]
    public async Task A_legal_but_different_kdf_cost_fails_the_key_unwrap()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        // 8 MiB / 2 passes / 1 lane is perfectly legal, just not what this vault was written with.
        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(24), 2);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "kdfIterations was changed from 1 to 2");
        VaultAssert.Failure(error, VaultErrorCode.AuthenticationFailed, "legal but different KDF cost");
    }

    [Theory]
    [InlineData(0UL, VaultErrorCode.HeaderCorrupt)]
    [InlineData(65551UL, VaultErrorCode.HeaderCorrupt)]                 // one below the minimum
    [InlineData(131104UL, VaultErrorCode.Truncated)]                    // legal, but twice what this vault holds
    [InlineData((64UL * 1024 * 1024) + 17, VaultErrorCode.HeaderCorrupt)] // one above the maximum
    [InlineData(1UL << 62, VaultErrorCode.HeaderCorrupt)]               // absurd, must be rejected instantly
    [InlineData(ulong.MaxValue, VaultErrorCode.HeaderCorrupt)]
    public async Task An_out_of_range_index_length_is_refused_before_the_key_derivation(ulong indexLength, VaultErrorCode expected)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt64LittleEndian(mutated.AsSpan(148), indexLength);

        string because = $"indexLength was set to {indexLength}";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);
        watch.Stop();

        VaultAssert.Failure(error, expected, because);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(5),
            $"{because}: the header check must reject this before any key derivation (took {watch.Elapsed}).");
    }

    /// <summary>The verdict FORMAT.md demands for a vault whose header byte at an offset was changed.</summary>
    /// <param name="offset">Offset of the changed byte.</param>
    /// <param name="image">The whole mutated vault image.</param>
    /// <param name="fileLength">Length of the mutated file.</param>
    private static Verdict Expect(int offset, byte[] image, long fileLength)
    {
        ReadOnlySpan<byte> header = image.AsSpan(0, VaultHeader.Size);

        // Section 3.1, in order. Every step below runs before any key derivation.
        if (!header[..8].SequenceEqual(VaultHeader.Magic))
        {
            return Verdict.Rejected(VaultErrorCode.NotAVault);
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (version == 0)
        {
            return Verdict.Rejected(VaultErrorCode.HeaderCorrupt);
        }

        if (version > 1)
        {
            return Verdict.Rejected(VaultErrorCode.UnsupportedVersion);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) != VaultHeader.Size ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[156..]) != 0)
        {
            return Verdict.Rejected(VaultErrorCode.HeaderCorrupt);
        }

        if ((BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) & 0xFFFFu) != 0 || header[16] != 1 || header[17] != 1)
        {
            return Verdict.Rejected(VaultErrorCode.UnsupportedParameters);
        }

        var kdf = new KdfParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[28..]));
        if (!kdf.IsValid)
        {
            return Verdict.Rejected(VaultErrorCode.UnsupportedParameters);
        }

        ulong indexLength = BinaryPrimitives.ReadUInt64LittleEndian(header[148..]);
        if (indexLength < (ulong)VaultLimits.MinIndexLength || indexLength > (ulong)VaultLimits.MaxIndexLength)
        {
            return Verdict.Rejected(VaultErrorCode.HeaderCorrupt);
        }

        if (fileLength < VaultHeader.Size + (2 * (long)indexLength))
        {
            return Verdict.Rejected(VaultErrorCode.Truncated);
        }

        if (header.Slice(124, 12).SequenceEqual(header.Slice(136, 12)))
        {
            return Verdict.Rejected(VaultErrorCode.HeaderCorrupt);
        }

        // The header passed every structural rule, so the AEADs decide. Section 2.6 says which one:
        return offset switch
        {
            // The index nonce is zeroed in indexAAD, so only the primary index fails; the copy still
            // authenticates and the vault opens with "save to repair" set (section 4.1).
            >= 124 and < 136 => Verdict.OpensFromCopy,

            // The copy nonce is likewise zeroed; the primary index is untouched, so nothing is noticed.
            >= 136 and < 148 => Verdict.Opens,

            // indexLength is inside indexAAD and decides how many bytes are read; both copies fail.
            >= 148 and < 156 => Verdict.Rejected(VaultErrorCode.IndexCorrupt),

            // Everything else is inside wrapAAD or is the wrapped key itself.
            _ => Verdict.Rejected(VaultErrorCode.AuthenticationFailed),
        };
    }

    /// <summary>What opening a mutated vault must do.</summary>
    /// <param name="Code">The error code, or <see langword="null"/> when the vault must still open.</param>
    /// <param name="FromIndexCopy">Whether a successful open must report that it used the index copy.</param>
    private readonly record struct Verdict(VaultErrorCode? Code, bool FromIndexCopy)
    {
        /// <summary>The vault opens normally.</summary>
        public static Verdict Opens => new(null, false);

        /// <summary>The vault opens, but from the index copy.</summary>
        public static Verdict OpensFromCopy => new(null, true);

        /// <summary>The vault is refused with a code.</summary>
        /// <param name="code">The code FORMAT.md demands.</param>
        public static Verdict Rejected(VaultErrorCode code) => new(code, false);
    }
}
