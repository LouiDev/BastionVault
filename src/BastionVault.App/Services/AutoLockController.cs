using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>Locks the open vault on idle, session lock, suspend and log-off.</summary>
public interface IAutoLockController
{
    /// <summary>The session to lock, or <see langword="null"/> when no vault is open.</summary>
    IVaultSession? Session { get; set; }

    /// <summary>Re-reads <see cref="AppSettings.AutoLockMinutes"/> and turns idle polling on or off.</summary>
    void ApplySettings();

    /// <summary>
    /// Runs a lock that was deferred because the session was busy. Call it when a long operation
    /// finishes; it does nothing when no lock is waiting.
    /// </summary>
    void ResumeDeferred();

    /// <summary>Raised on the UI thread after a vault was locked automatically.</summary>
    event EventHandler<AutoLockReason>? Locked;
}

/// <summary>Why the vault locked itself.</summary>
public enum AutoLockReason
{
    /// <summary>The machine was idle for longer than the configured time.</summary>
    Idle,

    /// <summary>The workstation was locked or a remote session disconnected.</summary>
    SessionLocked,

    /// <summary>The machine is suspending.</summary>
    Suspending,

    /// <summary>The user is logging off or the machine is shutting down.</summary>
    SessionEnding,
}

/// <summary>
/// Auto-lock (UI-CONTRACT.md section 4). It locks and nothing else: it never saves and never
/// prompts, because both would need a decision from a user who is not there. Unsaved work stays
/// in the session and comes back on unlock (FORMAT.md section 8.8).
/// </summary>
public sealed class AutoLockController : IAutoLockController, IDisposable
{
    private readonly IIdleMonitor _idle;
    private readonly ISystemEvents _system;
    private readonly ISettingsService _settings;
    private readonly ILog _log;
    private IVaultSession? _session;
    private AutoLockReason? _deferred;
    private bool _disposed;

    /// <summary>Wires the controller to the idle monitor and the system events.</summary>
    /// <param name="idle">System-wide idle monitor.</param>
    /// <param name="system">Session and power notifications.</param>
    /// <param name="settings">Settings, for the idle threshold.</param>
    /// <param name="log">Log.</param>
    public AutoLockController(IIdleMonitor idle, ISystemEvents system, ISettingsService settings, ILog log)
    {
        _idle = idle;
        _system = system;
        _settings = settings;
        _log = log;

        _idle.IdleThresholdReached += OnIdle;
        _system.SessionLocked += OnSessionLocked;
        _system.Suspending += OnSuspending;
        _system.SessionEnding += OnSessionEnding;
        _settings.Changed += OnSettingsChanged;
    }

    /// <inheritdoc />
    public event EventHandler<AutoLockReason>? Locked;

    /// <inheritdoc />
    public IVaultSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            _deferred = null;
            ApplySettings();
        }
    }

    /// <summary>
    /// Why a lock is waiting for the running operation to finish, or <see langword="null"/> when
    /// none is. Exposed for tests.
    /// </summary>
    internal AutoLockReason? Deferred => _deferred;

    /// <summary>
    /// Runs a lock that was deferred because the session was busy. The shell calls this when an
    /// operation finishes.
    /// </summary>
    public void ResumeDeferred()
    {
        if (_deferred is { } reason)
        {
            LockNow(reason);
        }
    }

    /// <inheritdoc />
    public void ApplySettings()
    {
        int minutes = _settings.Current.AutoLockMinutes;
        _idle.Threshold = TimeSpan.FromMinutes(Math.Max(0, minutes));
        _idle.Enabled = minutes > 0 && _session is not null;
    }

    /// <summary>Unhooks every subscription.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idle.IdleThresholdReached -= OnIdle;
        _system.SessionLocked -= OnSessionLocked;
        _system.Suspending -= OnSuspending;
        _system.SessionEnding -= OnSessionEnding;
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();

    private void OnIdle(object? sender, EventArgs e) => LockNow(AutoLockReason.Idle);

    private void OnSessionLocked(object? sender, EventArgs e) => LockNow(AutoLockReason.SessionLocked);

    private void OnSuspending(object? sender, EventArgs e) => LockNow(AutoLockReason.Suspending);

    private void OnSessionEnding(object? sender, EventArgs e) => LockNow(AutoLockReason.SessionEnding);

    private void LockNow(AutoLockReason reason)
    {
        IVaultSession? session = _session;
        if (session is null || session.IsLocked)
        {
            _deferred = null;
            return;
        }

        // Locking zeroes the key material under whatever is running. Nothing serialises Lock()
        // against SaveAsync, ImportAsync, ExportAsync or VerifyAsync, so an idle auto-lock - the
        // case where a long unattended operation is exactly what is running - would abort a save
        // mid-flight or make a verify report a false verdict. The lock is deferred until the
        // operation finishes; a log-off is the one notification that gets no second chance, so it
        // still locks immediately.
        if (reason != AutoLockReason.SessionEnding && session.IsBusy)
        {
            _deferred = reason;
            _log.Info($"Auto-lock deferred until the running operation finishes ({reason}).");
            return;
        }

        _deferred = null;
        session.Lock();
        _log.Info($"Vault locked automatically ({reason}).");
        Locked?.Invoke(this, reason);
    }
}
