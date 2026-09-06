using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>
/// Production <see cref="IKdfPreflight"/>: a thin pass-through to <see cref="KdfPreflight"/>. The
/// Debug-only test hook <c>--test-installed-memory=&lt;bytes&gt;</c> substitutes a machine size so the
/// warning states can be screenshotted on a machine that would never show them.
/// </summary>
public sealed class KdfPreflightService : IKdfPreflight
{
    private readonly long? _installedOverride;

    /// <summary>Creates the service for this machine.</summary>
    public KdfPreflightService()
    {
    }

    /// <summary>Creates the service for a pretend machine with <paramref name="installedBytes"/> of memory.</summary>
    /// <param name="installedBytes">Installed memory to report instead of measuring.</param>
    public KdfPreflightService(long installedBytes) => _installedOverride = installedBytes;

    /// <inheritdoc />
    public KdfPreflightResult Check(KdfParameters parameters) =>
        _installedOverride is { } installed
            ? KdfPreflight.Check(parameters, installed)
            : KdfPreflight.Check(parameters);
}
