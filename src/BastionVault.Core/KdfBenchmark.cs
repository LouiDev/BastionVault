using System.Diagnostics;
using System.Security.Cryptography;
using BastionVault.Core.Crypto;

namespace BastionVault.Core;

/// <summary>
/// Estimates how long the Argon2id key derivation will take on this machine, so the UI can
/// show a measured number instead of the reference numbers of FORMAT.md §7.
/// </summary>
public static class KdfBenchmark
{
    /// <summary>Memory cost of the probe run in KiB (32 MiB), rounded down to a multiple of <c>4 * parallelism</c>.</summary>
    private const uint ProbeMemoryKiB = 32 * 1024;

    /// <summary>Number of probe runs; the fastest one is used, so a scheduling hiccup does not inflate the estimate.</summary>
    private const int ProbeRuns = 2;

    private static ReadOnlySpan<byte> ProbePassword => "bastion/v1/kdf-benchmark"u8;

    private static ReadOnlySpan<byte> ProbeSalt => "bastion-benchmark-salt-00000000!"u8;

    /// <summary>
    /// Measures a small Argon2id run on this machine and scales the result to <paramref name="parameters"/>.
    /// </summary>
    /// <param name="parameters">The cost parameters to estimate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The estimated wall-clock duration of one derivation with those parameters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.UnsupportedParameters"/> — the parameters violate FORMAT.md §7.</exception>
    public static Task<TimeSpan> EstimateAsync(KdfParameters parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        return Task.Run(() => Measure(parameters, ct), ct);
    }

    /// <summary>
    /// Runs the probe and scales linearly in memory and passes: Argon2 touches
    /// <c>memory * iterations</c> blocks, and the per-block cost is what the probe measures.
    /// </summary>
    private static TimeSpan Measure(KdfParameters parameters, CancellationToken ct)
    {
        uint step = 4 * parameters.Parallelism;
        uint probeMemoryKiB = ProbeMemoryKiB - (ProbeMemoryKiB % step);

        long fastest = long.MaxValue;
        for (int run = 0; run < ProbeRuns; run++)
        {
            ct.ThrowIfCancellationRequested();

            long started = Stopwatch.GetTimestamp();
            byte[] probe = Argon2.Hash(
                Argon2Type.Id, ProbePassword, ProbeSalt, default, default,
                probeMemoryKiB, 1, parameters.Parallelism, 32, ct);
            long elapsed = Stopwatch.GetTimestamp() - started;
            CryptographicOperations.ZeroMemory(probe);

            if (elapsed < fastest)
            {
                fastest = elapsed;
            }
        }

        double ticksPerKiBPass = (double)fastest / probeMemoryKiB;
        double seconds = ticksPerKiBPass * parameters.MemoryKiB * parameters.Iterations / Stopwatch.Frequency;
        long estimate = (long)Math.Min(seconds * TimeSpan.TicksPerSecond, long.MaxValue / 2.0);
        return TimeSpan.FromTicks(Math.Max(1, estimate));
    }
}
