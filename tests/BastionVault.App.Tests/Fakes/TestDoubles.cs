using System.Collections.Concurrent;
using BastionVault.App.Services;
using BastionVault.Core;

namespace BastionVault.App.Tests.Fakes;

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
/// <summary>
/// An <see cref="IKdfPreflight"/> for a machine of a chosen size. The default, 0 installed bytes, is the
/// "nothing could be measured" case of FORMAT.md section 3.1 step 9 and lets everything through.
/// </summary>
public sealed class FakeKdfPreflight : IKdfPreflight
{
    /// <summary>Installed memory the fake machine reports; 0 means unmeasurable.</summary>
    public long InstalledBytes { get; set; }

    /// <summary>Every parameter set that was asked about, in order.</summary>
    public List<KdfParameters> Asked { get; } = [];

    /// <inheritdoc />
    public KdfPreflightResult Check(KdfParameters parameters)
    {
        Asked.Add(parameters);
        return KdfPreflight.Check(parameters, InstalledBytes);
    }
}

public sealed class DisposeFlag : IDisposable
{
    /// <summary>True once <see cref="Dispose"/> ran.</summary>
    public bool Disposed { get; private set; }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>
/// An <see cref="IVideoThumbnailer"/> that answers from a script: a fixed probe, or a hold that lets a
/// test cancel the caller while the probe is "running". It records what it was asked so tests can
/// check the stream was seekable and the hint came from the file name.
/// </summary>
public sealed class FakeVideoThumbnailer : IVideoThumbnailer
{
    /// <summary>The probe every call returns; <see langword="null"/> plays an unrecognised container.</summary>
    public VideoProbe? Result { get; set; }

    /// <summary>When set, a call waits on this before answering, so a test can cancel it mid-flight.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Content-type hints received, in order.</summary>
    public List<string?> ContentTypes { get; } = [];

    /// <summary>Maximum widths received, in order.</summary>
    public List<int> MaxWidths { get; } = [];

    /// <summary>Whether every stream handed in reported <see cref="Stream.CanSeek"/>.</summary>
    public bool AllStreamsSeekable { get; private set; } = true;

    /// <summary>Number of calls so far.</summary>
    public int Calls { get; private set; }

    /// <summary>A 2x2 BGRA frame that is easy to recognise in assertions.</summary>
    public static VideoFrame SampleFrame() => new(
        [0x10, 0x20, 0x30, 0xFF, 0x11, 0x21, 0x31, 0xFF, 0x12, 0x22, 0x32, 0xFF, 0x13, 0x23, 0x33, 0xFF], 2, 2);

    /// <inheritdoc />
    public async Task<VideoProbe?> ProbeAsync(Stream video, string? contentType, int maxWidth, CancellationToken ct)
    {
        Calls++;
        ContentTypes.Add(contentType);
        MaxWidths.Add(maxWidth);
        AllStreamsSeekable &= video.CanSeek;

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return Result;
    }
}
