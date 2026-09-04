using System.Buffers.Binary;
using System.Diagnostics;
using BastionVault.Core.Format;
using BastionVault.Core.Session;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The limits table of FORMAT.md section 7 seen from the outside: a header may declare any number it
/// likes, and the reader has to answer with a documented error before it allocates memory, derives a
/// key or reads a byte it has not proved to be there.
/// </summary>
[Collection(VaultTestCollection.Name)]
public sealed class LimitsTests
{
    /// <summary>How long "instantly" is allowed to be on a loaded build machine.</summary>
    private static readonly TimeSpan Instantly = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_header_that_demands_four_gibibytes_of_key_derivation_memory_is_answered_at_once()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(20), VaultLimits.MaxKdfMemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(28), 4);
        vault.Write(mutated);

        // The pre-flight of section 3.1 step 9 compares against the memory this machine has installed,
        // which does not move while the test runs: the verdict depends on the machine and on nothing
        // else, so it is predicted up front. Either way the answer is immediate, and either way the KDF
        // is asked for exactly the declared cost or not asked at all.
        long installed = Credentials.InstalledPhysicalMemoryBytes();
        long budget = (long)(installed * VaultLimits.KdfMemoryFractionOfInstalled);
        long required = (long)VaultLimits.MaxKdfMemoryKiB * 1024;
        bool refused = installed > 0 && required > budget;

        var kdf = new RecordingKeyDerivation();
        Stopwatch watch = Stopwatch.StartNew();
        VaultException error = await Assert.ThrowsAnyAsync<VaultException>(async () =>
        {
            await using IVaultSession session = await vault.OpenTargetAsync(kdf: kdf);
        });
        watch.Stop();

        Assert.True(
            watch.Elapsed < Instantly,
            $"a 4 GiB header must be answered in well under a second (took {watch.Elapsed}).");

        if (refused)
        {
            VaultAssert.Failure(error, VaultErrorCode.ResourceLimit, $"a 4 GiB key derivation with a {budget} byte budget");
            var resource = (VaultResourceException)error;
            Assert.Equal(required, resource.RequiredBytes);
            Assert.Equal(budget, resource.AvailableBytes);
            Assert.True(resource.AvailableBytes < required);
            Assert.Contains("installed", error.Message, StringComparison.Ordinal);
            Assert.Empty(kdf.Calls);
            return;
        }

        // The machine is large enough on paper: nothing is refused, and the reader asks for exactly what
        // the header says. The stub answers with a wrong tag, so the key unwrap is what fails.
        VaultAssert.Failure(
            error,
            VaultErrorCode.AuthenticationFailed,
            $"a 4 GiB key derivation this machine can afford ({budget} bytes of budget)");
        KdfParameters asked = Assert.Single(kdf.Calls);
        Assert.Equal(VaultLimits.MaxKdfMemoryKiB, asked.MemoryKiB);
        Assert.Equal(4u, asked.Parallelism);
    }

    [Theory]
    [InlineData(1UL << 62)]
    [InlineData((1UL << 62) + 16)]
    [InlineData(ulong.MaxValue)]
    [InlineData(long.MaxValue)]
    public async Task An_absurd_index_length_is_refused_without_touching_the_key_derivation(ulong indexLength)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt64LittleEndian(mutated.AsSpan(148), indexLength);

        var kdf = new RecordingKeyDerivation();
        string because = $"indexLength was set to {indexLength}";

        Stopwatch watch = Stopwatch.StartNew();
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because, kdf);
        watch.Stop();

        VaultAssert.Failure(error, VaultErrorCode.HeaderCorrupt, because);
        Assert.Empty(kdf.Calls);
        Assert.True(watch.Elapsed < Instantly, $"{because}: this is a range check, it took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(65u)]
    [InlineData(uint.MaxValue)]
    public async Task An_out_of_range_iteration_count_is_refused_without_touching_the_key_derivation(uint iterations)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(24), iterations);

        var kdf = new RecordingKeyDerivation();
        string because = $"kdfIterations was set to {iterations}";

        Stopwatch watch = Stopwatch.StartNew();
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because, kdf);
        watch.Stop();

        VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, because);
        Assert.Empty(kdf.Calls);
        Assert.True(watch.Elapsed < Instantly, $"{because}: this is a range check, it took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Reading_only_the_header_never_derives_a_key_and_reports_what_the_file_would_cost()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(20), VaultLimits.MaxKdfMemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(28), 4);
        vault.Write(mutated);

        var factory = new VaultFactory(new DeterministicRandomSource(1), new FixedClock(GoldenVault.Epoch));

        Stopwatch watch = Stopwatch.StartNew();
        VaultHeaderInfo info = await factory.ReadHeaderAsync(vault.TargetPath, CancellationToken.None);
        watch.Stop();

        Assert.Equal(1, info.FormatVersion);
        Assert.Equal(VaultLimits.MaxKdfMemoryKiB, info.Kdf.MemoryKiB);
        Assert.Equal((long)VaultLimits.MaxKdfMemoryKiB * 1024, info.RequiredMemoryBytes);
        Assert.Equal(mutated.LongLength, info.FileLength);
        Assert.Equal(VaultLimits.MinIndexLength, info.IndexLength);
        Assert.True(watch.Elapsed < Instantly, $"reading a header must not do any work (took {watch.Elapsed}).");
    }

    [Theory]
    // kdfMemoryKiB at 0, one below the minimum, the minimum, the maximum, one above the maximum.
    [InlineData(0u, 3u, 4u, false)]
    [InlineData(8188u, 3u, 4u, false)]
    [InlineData(8192u, 3u, 4u, true)]
    [InlineData(4194304u, 3u, 4u, true)]
    [InlineData(4194308u, 3u, 4u, false)]
    [InlineData(uint.MaxValue, 3u, 4u, false)]
    // kdfIterations at 0, 1, 64, 65.
    [InlineData(8192u, 0u, 1u, false)]
    [InlineData(8192u, 1u, 1u, true)]
    [InlineData(8192u, 64u, 1u, true)]
    [InlineData(8192u, 65u, 1u, false)]
    // kdfParallelism at 0, 1, 16, 17.
    [InlineData(8192u, 3u, 0u, false)]
    [InlineData(8192u, 3u, 1u, true)]
    [InlineData(8192u, 3u, 16u, true)]
    [InlineData(8192u, 3u, 17u, false)]
    // The two parallelism-dependent rules of the limits table.
    [InlineData(8194u, 3u, 1u, false)]
    [InlineData(8208u, 3u, 4u, true)]
    [InlineData(8196u, 3u, 4u, false)]
    [InlineData(8200u, 3u, 16u, false)]
    public void The_limits_table_is_enforced_at_every_boundary(uint memoryKiB, uint iterations, uint parallelism, bool valid)
    {
        var parameters = new KdfParameters(memoryKiB, iterations, parallelism);
        string because = $"m={memoryKiB} t={iterations} p={parallelism}";

        Assert.True(parameters.IsValid == valid, because);

        if (valid)
        {
            parameters.Validate();
            return;
        }

        VaultFormatException error = Assert.Throws<VaultFormatException>(parameters.Validate);
        VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, because);
    }

    [Fact]
    public void Every_preset_of_the_limits_table_is_valid_and_round_trips()
    {
        foreach (KdfPreset preset in Enum.GetValues<KdfPreset>())
        {
            KdfParameters parameters = KdfParameters.FromPreset(preset);
            parameters.Validate();
            Assert.Equal(preset, parameters.MatchingPreset);
            Assert.Equal(parameters.MemoryKiB * 1024L, parameters.MemoryBytes);
        }

        Assert.Equal(KdfPreset.Standard, KdfParameters.Default.MatchingPreset);
        Assert.Null(new KdfParameters(8192, 1, 1).MatchingPreset);
    }

    [Fact]
    public async Task Creating_a_vault_with_parameters_outside_the_table_is_refused_before_any_file_is_written()
    {
        using var work = new TempDirectory("limits-create");
        string path = work.File("rejected.bastion");

        var kdf = new RecordingKeyDerivation();
        var factory = new VaultFactory(new DeterministicRandomSource(2), new FixedClock(GoldenVault.Epoch), null, kdf);

        using Passphrase password = Passphrase.FromString(TamperVault.Password);
        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(
            () => factory.CreateAsync(path, password, null, new KdfParameters(8192, 65, 1), null, CancellationToken.None));

        VaultAssert.Failure(error, VaultErrorCode.UnsupportedParameters, "65 iterations");
        Assert.Empty(kdf.Calls);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(work.Path));
    }
}
