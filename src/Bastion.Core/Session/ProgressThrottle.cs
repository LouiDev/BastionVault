namespace Bastion.Core.Session;

/// <summary>
/// Rate-limits progress at the source per API.md rule 6: at most one report per
/// <c>max(4 MiB, 1 % of BytesTotal)</c>, plus one at the start, one at completion and one for the
/// transition into a phase that no longer honours the cancellation token.
/// </summary>
internal sealed class ProgressThrottle
{
    private const long MinimumStep = 4L * 1024 * 1024;

    private readonly IProgress<VaultProgress>? _sink;
    private readonly VaultOperation _operation;

    private long _step;
    private long _bytesTotal;
    private int _itemsTotal;
    private long _lastReportedBytes;
    private bool _completed;
    private bool _announcedNonCancellable;

    /// <summary>Creates a throttle for one operation.</summary>
    /// <param name="sink">The progress sink of the caller, or <see langword="null"/>.</param>
    /// <param name="operation">Operation being reported.</param>
    /// <param name="bytesTotal">Total bytes, or 0 when unknown.</param>
    /// <param name="itemsTotal">Total items, or 0 when unknown.</param>
    public ProgressThrottle(IProgress<VaultProgress>? sink, VaultOperation operation, long bytesTotal = 0, int itemsTotal = 0)
    {
        _sink = sink;
        _operation = operation;
        _bytesTotal = bytesTotal;
        _itemsTotal = itemsTotal;
        _step = Math.Max(MinimumStep, bytesTotal / 100);
    }

    /// <summary>True when nobody is listening, so callers can skip the bookkeeping entirely.</summary>
    public bool IsIdle => _sink is null;

    /// <summary>Updates the totals once they are known (after an import walk, for example).</summary>
    /// <param name="bytesTotal">Total bytes.</param>
    /// <param name="itemsTotal">Total items.</param>
    public void SetTotals(long bytesTotal, int itemsTotal)
    {
        _bytesTotal = bytesTotal;
        _itemsTotal = itemsTotal;
        _step = Math.Max(MinimumStep, bytesTotal / 100);
    }

    /// <summary>Emits the mandatory report at the start of the operation.</summary>
    /// <param name="currentItem">Item the operation begins with.</param>
    /// <param name="isCancellable">False while the token cannot be honoured.</param>
    public void Start(string? currentItem = null, bool isCancellable = true)
    {
        _lastReportedBytes = 0;
        Emit(0, 0, currentItem, isCancellable);
    }

    /// <summary>Emits a report when enough bytes have passed since the last one.</summary>
    /// <param name="bytesDone">Bytes processed so far.</param>
    /// <param name="itemsDone">Items completed so far.</param>
    /// <param name="currentItem">Item currently being processed.</param>
    /// <param name="isCancellable">False while the token cannot be honoured.</param>
    public void Report(long bytesDone, int itemsDone, string? currentItem, bool isCancellable = true)
    {
        if (_sink is null || _completed)
        {
            return;
        }

        // The moment an operation stops honouring the token is a state change, not a byte count: the UI
        // has to take Cancel away right then. It is emitted once, whatever the throttle would say.
        bool announce = !isCancellable && !_announcedNonCancellable;
        if (!announce && bytesDone - _lastReportedBytes < _step)
        {
            return;
        }

        _announcedNonCancellable |= !isCancellable;
        _lastReportedBytes = bytesDone;
        Emit(bytesDone, itemsDone, currentItem, isCancellable);
    }

    /// <summary>Emits the mandatory report at the end of the operation. Idempotent.</summary>
    /// <param name="bytesDone">Bytes processed.</param>
    /// <param name="itemsDone">Items completed.</param>
    /// <param name="currentItem">Last item, when useful.</param>
    public void Complete(long bytesDone, int itemsDone, string? currentItem = null)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        Emit(bytesDone, itemsDone, currentItem, false);
    }

    /// <summary>Sends one report.</summary>
    /// <param name="bytesDone">Bytes processed.</param>
    /// <param name="itemsDone">Items completed.</param>
    /// <param name="currentItem">Item being processed.</param>
    /// <param name="isCancellable">Whether the token is honoured right now.</param>
    private void Emit(long bytesDone, int itemsDone, string? currentItem, bool isCancellable) =>
        _sink?.Report(new VaultProgress(_operation, bytesDone, _bytesTotal, itemsDone, _itemsTotal, currentItem, isCancellable));
}
