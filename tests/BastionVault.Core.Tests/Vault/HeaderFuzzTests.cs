using System.Buffers.Binary;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// Fuzzes the header parser of FORMAT.md section 3. The exhaustive single-bit sweep over one reference
/// header lives in <c>Format/VaultHeaderTests</c>; this class asks the two questions a sweep cannot
/// answer: does the parser survive arbitrary bytes, arbitrary lengths and arbitrary field combinations
/// with nothing but the documented error codes, and is everything it accepts something the writer could
/// have produced.
/// </summary>
/// <remarks>
/// The index parser has its own fuzz suite under <c>Format/IndexSerializerFuzzTests</c>.
/// </remarks>
public sealed class HeaderFuzzTests
{
    /// <summary>Number of random inputs per fuzz case; the seams are seeded, so every run is identical.</summary>
    private const int Iterations = 4000;

    /// <summary>The only codes FORMAT.md section 3.1 allows the parser to report.</summary>
    private static readonly VaultErrorCode[] Documented =
    [
        VaultErrorCode.NotAVault,
        VaultErrorCode.UnsupportedVersion,
        VaultErrorCode.UnsupportedParameters,
        VaultErrorCode.HeaderCorrupt,
        VaultErrorCode.Truncated,
    ];

    [Fact]
    public void Parsing_random_bytes_of_any_length_only_ever_reports_a_documented_code()
    {
        var random = new Random(20260101);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] bytes = new byte[random.Next(0, 3 * VaultHeader.Size)];
            random.NextBytes(bytes);
            long fileLength = RandomLength(random, bytes.Length);

            VaultFormatException error = Assert.Throws<VaultFormatException>(
                () => VaultHeader.Parse(bytes, fileLength));

            Assert.Contains(error.Code, Documented);
        }
    }

    [Fact]
    public void Parsing_random_fields_behind_a_valid_magic_only_ever_reports_a_documented_code()
    {
        var random = new Random(20260102);
        int accepted = 0;

        for (int i = 0; i < Iterations; i++)
        {
            byte[] bytes = Reference();
            int mutations = random.Next(1, 8);
            for (int m = 0; m < mutations; m++)
            {
                // Never touch the magic: the interesting verdicts all live behind it.
                bytes[random.Next(8, VaultHeader.Size)] = (byte)random.Next(256);
            }

            long fileLength = RandomLength(random, bytes.Length);
            VaultHeader? parsed = TryParse(bytes, fileLength, out VaultFormatException? error);

            if (parsed is null)
            {
                Assert.Contains(error!.Code, Documented);
                Assert.NotEqual(VaultErrorCode.NotAVault, error.Code);
                continue;
            }

            accepted++;
            AssertRoundTrips(parsed, bytes);
        }

        Assert.True(accepted > 0, "the fuzzer never produced an acceptable header; it is testing nothing");
    }

    [Fact]
    public void Parsing_a_header_whose_numeric_fields_are_extreme_only_ever_reports_a_documented_code()
    {
        // The interesting values of every numeric field: 0, 1, the limit, one past the limit, all ones.
        uint[] interesting =
        [
            0, 1, 2, 7, 8, 15, 16, 17, 63, 64, 65,
            VaultLimits.MinKdfMemoryKiB - 1, VaultLimits.MinKdfMemoryKiB, VaultLimits.MinKdfMemoryKiB + 1,
            VaultLimits.MaxKdfMemoryKiB - 1, VaultLimits.MaxKdfMemoryKiB, VaultLimits.MaxKdfMemoryKiB + 1,
            int.MaxValue, uint.MaxValue,
        ];

        ulong[] lengths =
        [
            0, 1, 16, 65551, (ulong)VaultLimits.MinIndexLength, (ulong)VaultLimits.MinIndexLength + 1,
            (ulong)VaultLimits.MaxIndexLength - 1, (ulong)VaultLimits.MaxIndexLength, (ulong)VaultLimits.MaxIndexLength + 1,
            1UL << 31, 1UL << 62, long.MaxValue, ulong.MaxValue,
        ];

        var random = new Random(20260103);
        foreach (uint memory in interesting)
        {
            foreach (uint iterations in interesting)
            {
                byte[] bytes = Reference();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), memory);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), iterations);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), interesting[random.Next(interesting.Length)]);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(148), lengths[random.Next(lengths.Length)]);

                VaultHeader? parsed = TryParse(bytes, long.MaxValue, out VaultFormatException? error);
                if (parsed is null)
                {
                    Assert.Contains(error!.Code, Documented);
                    continue;
                }

                Assert.True(parsed.Kdf.IsValid, $"m={memory} t={iterations} was accepted although it is out of range");
                AssertRoundTrips(parsed, bytes);
            }
        }
    }

    [Fact]
    public async Task Opening_random_files_only_ever_reports_a_vault_error()
    {
        // API.md rule 5: no raw IOException or CryptographicException ever leaves Core, whatever the
        // bytes on disk look like.
        using var work = new TempDirectory("header-fuzz");
        var random = new Random(20260104);
        var factory = new VaultFactory(new DeterministicRandomSource(5), new FixedClock(GoldenVault.Epoch));

        for (int i = 0; i < 200; i++)
        {
            byte[] bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);
            if (i % 3 == 0 && bytes.Length >= VaultHeader.Magic.Length)
            {
                VaultHeader.Magic.CopyTo(bytes);
            }

            string path = work.File($"fuzz-{i}.bastion");
            await File.WriteAllBytesAsync(path, bytes);

            VaultException headerError = await Assert.ThrowsAnyAsync<VaultException>(
                () => factory.ReadHeaderAsync(path, CancellationToken.None));
            Assert.Contains(headerError.Code, Documented);

            using Passphrase password = Passphrase.FromString(TamperVault.Password);
            VaultException openError = await Assert.ThrowsAnyAsync<VaultException>(
                () => factory.OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None));
            Assert.Contains(openError.Code, Documented);
        }
    }

    /// <summary>A header a conforming writer could have written: the smallest legal vault.</summary>
    private static byte[] Reference()
    {
        var header = new VaultHeader
        {
            FormatVersion = 1,
            Flags = 0,
            Kdf = new KdfParameters(8192, 1, 1),
            KdfSalt = new byte[32],
            WrapNonce = new byte[12],
            WrappedVaultKey = new byte[48],
            IndexNonce = Filled(12, 0x11),
            IndexCopyNonce = Filled(12, 0x22),
            IndexLength = VaultLimits.MinIndexLength,
        };

        byte[] bytes = new byte[VaultHeader.Size];
        header.Write(bytes);
        return bytes;
    }

    /// <summary>A byte array of one repeated value.</summary>
    /// <param name="count">Number of bytes.</param>
    /// <param name="value">The value.</param>
    private static byte[] Filled(int count, byte value)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    /// <summary>A file length that is sometimes plausible and sometimes absurd.</summary>
    /// <param name="random">Random source.</param>
    /// <param name="bufferLength">Length of the buffer being parsed.</param>
    private static long RandomLength(Random random, int bufferLength) => random.Next(6) switch
    {
        0 => bufferLength,
        1 => 0,
        2 => VaultHeader.Size,
        3 => long.MaxValue,
        4 => random.NextInt64(0, 1L << 40),
        _ => VaultHeader.Size + (2L * VaultLimits.MinIndexLength),
    };

    /// <summary>Parses a header, returning <see langword="null"/> and the failure instead of throwing.</summary>
    /// <param name="bytes">Header bytes.</param>
    /// <param name="fileLength">Length of the file they came from.</param>
    /// <param name="error">The failure, when the header was rejected.</param>
    private static VaultHeader? TryParse(byte[] bytes, long fileLength, out VaultFormatException? error)
    {
        try
        {
            error = null;
            return VaultHeader.Parse(bytes, fileLength);
        }
        catch (VaultFormatException failure)
        {
            error = failure;
            return null;
        }
    }

    /// <summary>Asserts that writing an accepted header back reproduces the bytes it was parsed from.</summary>
    /// <param name="header">The parsed header.</param>
    /// <param name="original">The bytes it was parsed from.</param>
    private static void AssertRoundTrips(VaultHeader header, byte[] original)
    {
        byte[] rewritten = new byte[VaultHeader.Size];
        header.Write(rewritten);

        Assert.True(
            original.AsSpan().SequenceEqual(rewritten),
            $"the parser accepted bytes the writer cannot reproduce:\n{Convert.ToHexString(original)}\n{Convert.ToHexString(rewritten)}");
    }
}
