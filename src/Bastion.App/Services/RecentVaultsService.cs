using System.IO;
namespace Bastion.App.Services;

/// <summary>Document shape of the recent-vault store.</summary>
public sealed class RecentVaultsDocument
{
    /// <summary>Most recently opened first.</summary>
    public List<RecentVaultRecord> Items { get; set; } = [];
}

/// <summary>One stored recent-vault row.</summary>
public sealed class RecentVaultRecord
{
    /// <summary>Full path of the vault file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>When it was last opened.</summary>
    public DateTimeOffset LastOpenedUtc { get; set; }

    /// <summary>Remembered keyfile path, only when the user opted in.</summary>
    public string? KeyFilePath { get; set; }
}

/// <summary>
/// The recent-vault list. Opt-out per <see cref="AppSettings.RememberRecentVaults"/>, DPAPI
/// protected, and never mirrored into the Windows recent items or a jump list
/// (THREAT-MODEL.md A5).
/// </summary>
public sealed class RecentVaultsService : IRecentVaults
{
    private const int MaxItems = 12;

    private readonly DpapiStore<RecentVaultsDocument> _store;
    private readonly ISettingsService _settings;
    private RecentVaultsDocument _document;

    /// <summary>Creates the service over the default store file.</summary>
    /// <param name="settings">Settings, for the remember-recents and remember-keyfile switches.</param>
    /// <param name="log">Optional log.</param>
    public RecentVaultsService(ISettingsService settings, ILog? log = null)
        : this(settings, new DpapiStore<RecentVaultsDocument>(AppPaths.RecentFile, log))
    {
    }

    /// <summary>Creates the service over a specific store.</summary>
    /// <param name="settings">Settings, for the remember-recents and remember-keyfile switches.</param>
    /// <param name="store">Backing store.</param>
    public RecentVaultsService(ISettingsService settings, DpapiStore<RecentVaultsDocument> store)
    {
        _settings = settings;
        _store = store;
        _document = _store.Load();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<RecentVault> Items =>
        _settings.Current.RememberRecentVaults
            ? [.. _document.Items.Select(i => new RecentVault(i.Path, i.LastOpenedUtc, i.KeyFilePath))]
            : [];

    /// <inheritdoc />
    public void Touch(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_settings.Current.RememberRecentVaults)
        {
            return;
        }

        string full = FullPath(path);
        RecentVaultRecord? existing = Find(full);
        if (existing is not null)
        {
            _document.Items.Remove(existing);
        }

        _document.Items.Insert(0, new RecentVaultRecord
        {
            Path = full,
            LastOpenedUtc = DateTimeOffset.UtcNow,
            KeyFilePath = _settings.Current.RememberKeyFilePaths ? existing?.KeyFilePath : null,
        });

        if (_document.Items.Count > MaxItems)
        {
            _document.Items.RemoveRange(MaxItems, _document.Items.Count - MaxItems);
        }

        Commit();
    }

    /// <inheritdoc />
    public void RememberKeyFile(string path, string? keyFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        RecentVaultRecord? existing = Find(FullPath(path));
        if (existing is null)
        {
            return;
        }

        existing.KeyFilePath = _settings.Current.RememberKeyFilePaths ? keyFilePath : null;
        Commit();
    }

    /// <inheritdoc />
    public void Forget(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        RecentVaultRecord? existing = Find(FullPath(path));
        if (existing is null)
        {
            return;
        }

        _document.Items.Remove(existing);
        Commit();
    }

    /// <inheritdoc />
    public void Clear()
    {
        _document = new RecentVaultsDocument();
        Commit();
    }

    private static string FullPath(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
    }

    private RecentVaultRecord? Find(string fullPath) =>
        _document.Items.FirstOrDefault(i => string.Equals(i.Path, fullPath, StringComparison.OrdinalIgnoreCase));

    private void Commit()
    {
        _store.Save(_document);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Document shape of the rollback record.</summary>
public sealed class RollbackDocument
{
    /// <summary>Highest save counter seen per vault id.</summary>
    public Dictionary<string, ulong> Counters { get; set; } = [];
}

/// <summary>
/// Records the highest save counter seen per vault on this machine so a whole-file rollback can
/// be reported at unlock. This is the only defence against A2's residual risk, and it is a
/// warning, not a guarantee.
/// </summary>
public sealed class RollbackGuard : IRollbackGuard
{
    private readonly DpapiStore<RollbackDocument> _store;
    private readonly RollbackDocument _document;

    /// <summary>Creates the guard over the default store file.</summary>
    /// <param name="log">Optional log.</param>
    public RollbackGuard(ILog? log = null)
        : this(new DpapiStore<RollbackDocument>(AppPaths.RollbackFile, log))
    {
    }

    /// <summary>Creates the guard over a specific store.</summary>
    /// <param name="store">Backing store.</param>
    public RollbackGuard(DpapiStore<RollbackDocument> store)
    {
        _store = store;
        _document = _store.Load();
    }

    /// <inheritdoc />
    public ulong? LastSeenCounter(string vaultIdHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultIdHex);
        return _document.Counters.TryGetValue(vaultIdHex, out ulong counter) ? counter : null;
    }

    /// <inheritdoc />
    public void Record(string vaultIdHex, ulong counter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultIdHex);

        if (_document.Counters.TryGetValue(vaultIdHex, out ulong known) && known >= counter)
        {
            return;
        }

        _document.Counters[vaultIdHex] = counter;
        _store.Save(_document);
    }
}
