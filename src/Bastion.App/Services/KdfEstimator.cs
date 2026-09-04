using System.Collections.Concurrent;
using Bastion.Core;

namespace Bastion.App.Services;

/// <summary>
/// Wraps <see cref="KdfBenchmark"/> and caches one measurement per parameter set for the life of
/// the process. An estimate is a number in a dialog: it must never throw and never block the UI,
/// so a benchmark that is unavailable falls back to a linear model of Argon2id's cost
/// (memory times passes) rather than leaving the dialog without a number.
/// </summary>
public sealed class KdfEstimator : IKdfEstimator
{
    /// <summary>Milliseconds per MiB per pass used by the fallback model.</summary>
    private const double MillisecondsPerMebibytePerPass = 0.55;

    private readonly ConcurrentDictionary<KdfParameters, TimeSpan> _cache = new();
    private readonly ILog? _log;

    /// <summary>Creates the estimator.</summary>
    /// <param name="log">Optional log.</param>
    public KdfEstimator(ILog? log = null) => _log = log;

    /// <summary>The modelled cost used when the machine cannot be measured.</summary>
    /// <param name="parameters">Argon2id parameters.</param>
    public static TimeSpan Model(KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        double mebibytes = parameters.MemoryKiB / 1024.0;
        double milliseconds = mebibytes * parameters.Iterations * MillisecondsPerMebibytePerPass;
        return TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
    }

    /// <inheritdoc />
    public async Task<TimeSpan> EstimateAsync(KdfParameters parameters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (_cache.TryGetValue(parameters, out TimeSpan cached))
        {
            return cached;
        }

        TimeSpan measured;
        try
        {
            // Core's benchmark really does derive a key, and it makes no promise about which
            // thread it burns. Task.Run keeps that off the UI thread no matter how it is written.
            measured = await Task.Run(() => KdfBenchmark.EstimateAsync(parameters, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes NotImplementedException while Core's benchmark is still a stub.
            _log?.Warn("The key-derivation benchmark is unavailable; the modelled estimate is used.", ex);
            measured = Model(parameters);
        }

        if (measured <= TimeSpan.Zero)
        {
            measured = Model(parameters);
        }

        _cache[parameters] = measured;
        return measured;
    }
}
