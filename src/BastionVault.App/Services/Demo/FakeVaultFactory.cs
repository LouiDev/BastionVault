using BastionVault.Core;

namespace BastionVault.App.Services.Demo;

/// <summary>
/// The <c>--demo</c> factory. It hands out <see cref="FakeVaultSession"/>s, simulates the key
/// derivation with a visible non-cancellable phase, and accepts any password - the point of demo
/// mode is to exercise the UI, not the cryptography.
/// </summary>
public sealed class FakeVaultFactory : IVaultFactory
{
    private readonly ILog _log;

    /// <summary>Creates the factory.</summary>
    /// <param name="log">Log.</param>
    public FakeVaultFactory(ILog log) => _log = log;

    /// <inheritdoc />
    public Task<VaultHeaderInfo> ReadHeaderAsync(string path, CancellationToken ct)
    {
        KdfParameters kdf = KdfParameters.Default;
        return Task.FromResult(new VaultHeaderInfo(1, kdf, 268_435_456, 65_536, kdf.MemoryBytes));
    }

    /// <inheritdoc />
    public async Task<IVaultSession> CreateAsync(
        string path, Passphrase password, KeyFile? keyFile, KdfParameters kdf,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _log.Info("Demo mode: creating an in-memory vault.");
        await DeriveAsync(VaultOperation.Create, progress).ConfigureAwait(false);
        return new FakeVaultSession(path);
    }

    /// <inheritdoc />
    public async Task<IVaultSession> OpenAsync(
        string path, Passphrase password, KeyFile? keyFile, OpenOptions options,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _log.Info("Demo mode: opening an in-memory vault.");
        await DeriveAsync(VaultOperation.Open, progress).ConfigureAwait(false);
        return new FakeVaultSession(path);
    }

    /// <inheritdoc />
    public Task<long> SweepOrphansAsync(IEnumerable<string> directories, CancellationToken ct) => Task.FromResult(0L);

    private static async Task DeriveAsync(VaultOperation operation, IProgress<VaultProgress>? progress)
    {
        // The derivation is deliberately not cancellable, exactly like the real one, so the
        // unlock button's "Deriving key ..." state is visible in demo mode too.
        progress?.Report(new VaultProgress(VaultOperation.KeyDerivation, 0, 0, 0, 1, "deriving key", false));
        await Task.Delay(TimeSpan.FromMilliseconds(900), CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new VaultProgress(operation, 0, 0, 1, 1, "reading index", true));
    }
}
