using System.Windows.Threading;

namespace Bastion.App.Services;

/// <summary>
/// The one place a <see cref="Dispatcher"/> is touched. View models take
/// <see cref="IUiDispatcher"/> and stay testable without an STA thread.
/// </summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates a dispatcher wrapper over a WPF dispatcher.</summary>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    public UiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <inheritdoc />
    public bool CheckAccess() => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Always queue, even on the UI thread: callers rely on "at most one pending callback"
        // semantics (ThrottledProgress) and on not re-entering during an event handler.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    /// <inheritdoc />
    public void Send(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            // Send priority, and bounded: this runs on the SystemEvents thread while Windows is
            // waiting to suspend or end the session, so it must neither be queued behind ordinary
            // work nor block that thread indefinitely if the UI thread is stuck.
            _dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // The UI thread did not answer in time; nothing more can be done from here.
        }
        catch (TaskCanceledException)
        {
            // The dispatcher is shutting down.
        }
    }

    /// <inheritdoc />
    public IDisposable PostDelayed(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = delay < TimeSpan.Zero ? TimeSpan.Zero : delay,
        };

        timer.Tick += OnTick;
        timer.Start();
        return new TimerHandle(timer, OnTick);

        void OnTick(object? sender, EventArgs e)
        {
            timer.Stop();
            timer.Tick -= OnTick;
            action();
        }
    }

    private sealed class TimerHandle : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly EventHandler _tick;
        private bool _disposed;

        public TimerHandle(DispatcherTimer timer, EventHandler tick)
        {
            _timer = timer;
            _tick = tick;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= _tick;
        }
    }
}
