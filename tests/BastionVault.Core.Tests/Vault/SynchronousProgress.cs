namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// A progress sink that runs its callback on the reporting thread. <see cref="Progress{T}"/> posts to a
/// captured context, which makes "cancel at the third report" a race; this one makes it exact.
/// </summary>
internal sealed class SynchronousProgress : IProgress<VaultProgress>
{
    private readonly Action<VaultProgress> _callback;
    private readonly List<VaultProgress> _reports = [];

    /// <summary>Creates a sink.</summary>
    /// <param name="callback">Called inline for every report.</param>
    public SynchronousProgress(Action<VaultProgress> callback) => _callback = callback;

    /// <summary>Every report received so far, in order.</summary>
    public IReadOnlyList<VaultProgress> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    /// <inheritdoc />
    public void Report(VaultProgress value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }

        _callback(value);
    }
}
