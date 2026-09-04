using System.Collections.Concurrent;
using Bastion.App.Services;

namespace Bastion.App.Tests.Fakes;

/// <summary>An <see cref="IUiDispatcher"/> that runs everything inline, so tests stay deterministic.</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    /// <summary>Number of actions that were posted.</summary>
    public int PostCount { get; private set; }

    /// <inheritdoc />
    public bool CheckAccess() => true;

    /// <inheritdoc />
    public void Post(Action action)
    {
        PostCount++;
        action();
    }

    /// <inheritdoc />
    public void Send(Action action)
    {
        SendCount++;
        action();
    }

    /// <summary>Number of actions that were sent.</summary>
    public int SendCount { get; private set; }

    /// <inheritdoc />
    public IDisposable PostDelayed(TimeSpan delay, Action action)
    {
        PostCount++;
        action();
        return new NoopHandle();
    }

    private sealed class NoopHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// An <see cref="IUiDispatcher"/> that queues instead of running, so a test can decide exactly
/// when the "UI thread" gets a turn. This is what makes coalescing observable.
/// </summary>
public sealed class ManualDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    /// <summary>Number of callbacks still waiting.</summary>
    public int Pending => _queue.Count;

    /// <inheritdoc />
    public bool CheckAccess() => false;

    /// <inheritdoc />
    public void Post(Action action) => _queue.Enqueue(action);

    /// <inheritdoc />
    public void Send(Action action)
    {
        SendCount++;
        action();
    }

    /// <summary>Number of actions that bypassed the queue through Send.</summary>
    public int SendCount { get; private set; }

    /// <summary>Delay of the most recent <see cref="PostDelayed"/>, or null when there was none.</summary>
    public TimeSpan? LastDelay { get; private set; }

    /// <summary>How many callbacks were queued with a delay.</summary>
    public int DelayedCount { get; private set; }

    /// <inheritdoc />
    public IDisposable PostDelayed(TimeSpan delay, Action action)
    {
        LastDelay = delay;
        DelayedCount++;

        var handle = new Handle();
        _queue.Enqueue(() =>
        {
            if (!handle.Cancelled)
            {
                action();
            }
        });

        return handle;
    }

    private sealed class Handle : IDisposable
    {
        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }

    /// <summary>Runs every queued callback, in order.</summary>
    /// <returns>How many callbacks ran.</returns>
    public int Drain()
    {
        int count = 0;
        while (_queue.TryDequeue(out Action? action))
        {
            action();
            count++;
        }

        return count;
    }
}

/// <summary>A log that keeps its lines in memory.</summary>
public sealed class MemoryLog : ILog
{
    /// <summary>Everything that was logged, newest last.</summary>
    public List<string> Lines { get; } = [];

    /// <inheritdoc />
    public void Info(string message) => Lines.Add("INF " + message);

    /// <inheritdoc />
    public void Warn(string message, Exception? ex = null) => Lines.Add("WRN " + message);

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null) => Lines.Add("ERR " + message);
}

/// <summary>A settings service over an in-memory <see cref="AppSettings"/>.</summary>
public sealed class MemorySettings : ISettingsService
{
    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public AppSettings Current { get; } = new();

    /// <summary>Number of times <see cref="Save"/> was called.</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public void Save()
    {
        SaveCount++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A disposable that records whether it was released.</summary>
public sealed class DisposeFlag : IDisposable
{
    /// <summary>True once <see cref="Dispose"/> ran.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}
