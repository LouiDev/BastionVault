using Microsoft.Win32;

namespace BastionVault.App.Services;

/// <summary>
/// Session and power notifications from <see cref="SystemEvents"/>, marshalled onto the UI thread.
/// <see cref="SystemEvents"/> raises on its own hidden-window thread, so every handler here hops
/// through <see cref="IUiDispatcher"/> before the app reacts.
/// </summary>
public sealed class SystemEventsService : ISystemEvents, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    /// <summary>Subscribes to the system notifications.</summary>
    /// <param name="dispatcher">Used to marshal every notification onto the UI thread.</param>
    public SystemEventsService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    /// <inheritdoc />
    public event EventHandler? SessionLocked;

    /// <inheritdoc />
    public event EventHandler? Suspending;

    /// <inheritdoc />
    public event EventHandler? SessionEnding;

    /// <summary>Unsubscribes from the system notifications.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.RemoteDisconnect
            or SessionSwitchReason.ConsoleDisconnect)
        {
            _dispatcher.Post(() => SessionLocked?.Invoke(this, EventArgs.Empty));
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // Send, not Post: the machine is about to sleep and a queued background item may
            // never run, which would leave the vault keys in the hibernation file (UI-CONTRACT.md
            // section 4).
            _dispatcher.Send(() => Suspending?.Invoke(this, EventArgs.Empty));
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) =>
        // Send, not Post: Windows gives a short window before it terminates the process.
        _dispatcher.Send(() => SessionEnding?.Invoke(this, EventArgs.Empty));
}
