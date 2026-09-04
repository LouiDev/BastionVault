namespace BastionVault.Core.Tests.Crypto;

/// <summary>The measured KDF estimate the UI shows instead of the reference numbers of FORMAT.md section 7.</summary>
public sealed class KdfBenchmarkTests
{
    [Fact]
    public async Task EstimateAsync_ReturnsAPositiveDuration()
    {
        TimeSpan estimate = await KdfBenchmark.EstimateAsync(KdfParameters.FromPreset(KdfPreset.Fast), CancellationToken.None);

        Assert.True(estimate > TimeSpan.Zero, $"the estimate must be positive but was {estimate}.");
        Assert.True(estimate < TimeSpan.FromMinutes(10), $"the estimate is implausibly large: {estimate}.");
    }

    /// <summary>The estimate scales with the cost, so a more expensive preset must not look cheaper.</summary>
    [Fact]
    public async Task EstimateAsync_GrowsWithTheCostParameters()
    {
        TimeSpan fast = await KdfBenchmark.EstimateAsync(KdfParameters.FromPreset(KdfPreset.Fast), CancellationToken.None);
        TimeSpan strong = await KdfBenchmark.EstimateAsync(KdfParameters.FromPreset(KdfPreset.Strong), CancellationToken.None);

        Assert.True(strong > fast, $"Strong ({strong}) must be estimated above Fast ({fast}).");
    }

    [Fact]
    public async Task EstimateAsync_RejectsInvalidParameters()
    {
        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(
            () => KdfBenchmark.EstimateAsync(new KdfParameters(1024, 1, 4), CancellationToken.None));

        Assert.Equal(VaultErrorCode.UnsupportedParameters, error.Code);
    }

    [Fact]
    public async Task EstimateAsync_RejectsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => KdfBenchmark.EstimateAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task EstimateAsync_HonoursACancelledToken()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => KdfBenchmark.EstimateAsync(KdfParameters.FromPreset(KdfPreset.Fast), cts.Token));
    }

    /// <summary>Every lane count is a valid probe configuration, including the ones 32 MiB is not a multiple of.</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(16u)]
    public async Task EstimateAsync_WorksForEveryLaneCount(uint parallelism)
    {
        KdfParameters parameters = new(8192 * parallelism, 1, parallelism);

        TimeSpan estimate = await KdfBenchmark.EstimateAsync(parameters, CancellationToken.None);

        Assert.True(estimate > TimeSpan.Zero);
    }
}
