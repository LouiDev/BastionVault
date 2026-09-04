namespace Bastion.App.Services;

/// <summary>
/// An <see cref="IProgress{T}"/> that coalesces. Core rate-limits at the source, but a fast import
/// can still report more often than a screen can repaint, and every report crossing the
/// dispatcher costs a message. This keeps only the newest value and has at most one callback in
/// flight: while one is pending, further reports just overwrite the value.
/// <para>
/// On top of that there is a floor of <see cref="MinimumInterval"/> between two deliveries
/// (UI-CONTRACT.md section 1.2): the first report after a quiet moment is delivered at once, and
/// a report that arrives sooner than the floor allows is held on the dispatcher's timer until it
/// does. Without the floor the delivery rate is whatever the dispatcher can drain, which on a
/// fast machine with an idle UI thread means the progress dialog re-lays-out with no lower bound.
/// </para>
/// </summary>
/// <typeparam name="T">Type of the progress value.</typeparam>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    /// <summary>Shortest gap between two deliveries.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(80);

    private readonly IUiDispatcher _dispatcher;
    private readonly Action<T> _handler;
    private readonly Lock _gate = new();

    private T? _latest;
    private bool _hasValue;
    private bool _posted;
    private long? _lastDeliveryTicks;
    private IDisposable? _pending;

    /// <summary>Creates a coalescing progress sink.</summary>
    /// <param name="dispatcher">Marshals the callback onto the UI thread.</param>
    /// <param name="handler">Called on the UI thread with the newest value.</param>
    public ThrottledProgress(IUiDispatcher dispatcher, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(handler);

        _dispatcher = dispatcher;
        _handler = handler;
    }

    /// <summary>Number of callbacks actually delivered; reports that were coalesced away are not counted.</summary>
    public int DeliveredCount { get; private set; }

    /// <inheritdoc />
    public void Report(T value)
    {
        bool post;
        TimeSpan wait = TimeSpan.Zero;

        lock (_gate)
        {
            _latest = value;
            _hasValue = true;
            post = !_posted;
            _posted = true;

            if (post && _lastDeliveryTicks is { } last)
            {
                double since = Environment.TickCount64 - last;
                if (since < MinimumInterval.TotalMilliseconds)
                {
                    wait = TimeSpan.FromMilliseconds(MinimumInterval.TotalMilliseconds - since);
                }
            }
        }

        if (!post)
        {
            return;
        }

        if (wait <= TimeSpan.Zero)
        {
            _dispatcher.Post(Flush);
            return;
        }

        _pending = _dispatcher.PostDelayed(wait, Flush);
    }

    /// <summary>
    /// Delivers the newest value, if any, and re-arms. Public so a caller can force a final
    /// repaint when an operation ends.
    /// </summary>
    public void Flush()
    {
        IDisposable? pending = _pending;
        _pending = null;
        pending?.Dispose();

        T? value;
        lock (_gate)
        {
            _posted = false;
            if (!_hasValue)
            {
                return;
            }

            value = _latest;
            _hasValue = false;
            _latest = default;
            _lastDeliveryTicks = Environment.TickCount64;
        }

        DeliveredCount++;
        _handler(value!);
    }
}
