using System.Diagnostics;
using System.Globalization;
using BastionVault.App.Services;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels;

/// <summary>
/// The one place a long Core call is started from. It moves the work onto the thread pool,
/// coalesces progress through <see cref="ThrottledProgress{T}"/>, and turns raw byte counters into
/// the numbers the progress dialog and the status bar show: percent, throughput and an ETA that
/// only appears once it is worth trusting (UI-CONTRACT.md sections 1.2 and 7).
/// </summary>
public sealed partial class OperationViewModel : ObservableObject
{
    /// <summary>An ETA is only shown once the operation has been running this long.</summary>
    private static readonly TimeSpan EtaDelay = TimeSpan.FromSeconds(2);

    private readonly IUiDispatcher _dispatcher;
    private readonly ILog _log;
    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isModal;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _verb = string.Empty;

    [ObservableProperty]
    private string? _currentItem;

    [ObservableProperty]
    private int _itemsDone;

    [ObservableProperty]
    private int _itemsTotal;

    [ObservableProperty]
    private long _bytesDone;

    [ObservableProperty]
    private long _bytesTotal;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private bool _isCancellable = true;

    [ObservableProperty]
    private bool _cancelRequested;

    [ObservableProperty]
    private string? _throughput;

    [ObservableProperty]
    private string? _eta;

    [ObservableProperty]
    private string _cancelSemantics = string.Empty;

    /// <summary>Creates the runner.</summary>
    /// <param name="dispatcher">Marshals progress onto the UI thread.</param>
    /// <param name="log">Log.</param>
    public OperationViewModel(IUiDispatcher dispatcher, ILog log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>Raised when an operation starts, so the shell can show the progress dialog.</summary>
    public event EventHandler? Started;

    /// <summary>Raised when an operation finishes, whatever the outcome.</summary>
    public event EventHandler? Finished;

    /// <summary>True when the last completed operation ended in a cancellation.</summary>
    public bool WasCancelled { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool with progress plumbed through, and returns
    /// its result. A cancellation returns <see langword="default"/> and sets
    /// <see cref="WasCancelled"/>; every other failure propagates to the caller, which is the only
    /// place that knows how to phrase it.
    /// </summary>
    /// <typeparam name="T">Result of the operation.</typeparam>
    /// <param name="operation">Which Core operation this is; drives the verb and the cancel sentence.</param>
    /// <param name="title">Dialog title, already phrased as a verb.</param>
    /// <param name="work">The Core call.</param>
    /// <param name="isModal">True for Save, Save copy and Change credentials, which block the explorer.</param>
    /// <param name="ct">An outer token; the cancel command is linked to it.</param>
    public async Task<T?> RunAsync<T>(
        VaultOperation operation,
        string title,
        Func<IProgress<VaultProgress>, CancellationToken, Task<T>> work,
        bool isModal = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (IsRunning)
        {
            throw new InvalidOperationException("Another operation is already running.");
        }

        Begin(operation, title, isModal);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellation = linked;

        var progress = new ThrottledProgress<VaultProgress>(_dispatcher, Apply);

        try
        {
            return await Task.Run(() => work(progress, linked.Token), linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            WasCancelled = true;
            _log.Info($"{operation} was cancelled.");
            return default;
        }
        finally
        {
            progress.Flush();
            _cancellation = null;
            End();
        }
    }

    /// <summary>Runs an operation that produces no value.</summary>
    /// <param name="operation">Which Core operation this is.</param>
    /// <param name="title">Dialog title, already phrased as a verb.</param>
    /// <param name="work">The Core call.</param>
    /// <param name="isModal">True when the operation blocks the explorer.</param>
    /// <param name="ct">An outer token.</param>
    /// <returns>True when the operation ran to completion, false when it was cancelled.</returns>
    public async Task<bool> RunAsync(
        VaultOperation operation,
        string title,
        Func<IProgress<VaultProgress>, CancellationToken, Task> work,
        bool isModal = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await RunAsync<object?>(
            operation,
            title,
            async (progress, token) =>
            {
                await work(progress, token).ConfigureAwait(false);
                return null;
            },
            isModal,
            ct).ConfigureAwait(true);

        return !WasCancelled;
    }

    /// <summary>Asks the running operation to stop. Does nothing during a non-cancellable phase.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (!CanCancel())
        {
            return;
        }

        CancelRequested = true;
        _cancellation?.Cancel();
    }

    /// <summary>Formats a byte count the way every readout in Bastion Vault does.</summary>
    /// <param name="bytes">Number of bytes.</param>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string number = unit == 0
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture);
        return $"{number} {units[unit]}";
    }

    /// <summary>Formats a duration as the coarse "about" text the ETA line uses.</summary>
    /// <param name="remaining">Estimated time left.</param>
    public static string FormatEta(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromSeconds(5))
        {
            return "a few seconds left";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return $"{(int)remaining.TotalSeconds} seconds left";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
            return minutes == 1 ? "about a minute left" : $"about {minutes} minutes left";
        }

        int hours = (int)Math.Floor(remaining.TotalHours);
        int rest = (int)Math.Round(remaining.TotalMinutes - (hours * 60));
        return $"about {hours} h {rest} min left";
    }

    private static string VerbFor(VaultOperation operation) => operation switch
    {
        VaultOperation.Open => "Opening",
        VaultOperation.Create => "Creating",
        VaultOperation.Save => "Saving",
        VaultOperation.SaveCopy => "Writing a copy",
        VaultOperation.Import => "Importing",
        VaultOperation.Export => "Exporting",
        VaultOperation.Verify => "Verifying",
        VaultOperation.Recover => "Recovering",
        VaultOperation.ChangeCredentials => "Changing credentials",
        VaultOperation.Copy => "Copying",
        VaultOperation.KeyDerivation => "Deriving key",
        _ => "Working",
    };

    private static string CancelSemanticsFor(VaultOperation operation) => operation switch
    {
        VaultOperation.Import => "Cancelling discards everything this import staged; the vault is left as it was.",
        VaultOperation.Export => "Cancelling deletes the file being written; files already written stay on disk.",
        VaultOperation.Recover => "Cancelling deletes the file being written; files already recovered stay on disk.",
        VaultOperation.Save => "Cancelling before the swap leaves the vault untouched; after it the save finishes.",
        VaultOperation.SaveCopy => "Cancelling deletes the partial copy; the original vault is untouched.",
        VaultOperation.Verify => "Cancelling stops the check; nothing is changed either way.",
        VaultOperation.Create => "Cancelling leaves no file behind.",
        VaultOperation.ChangeCredentials => "Cancelling leaves the current password in place.",
        _ => "Cancelling leaves the vault as it was.",
    };

    private bool CanCancel() => IsRunning && IsCancellable && !CancelRequested;

    private void Begin(VaultOperation operation, string title, bool isModal)
    {
        Title = title;
        Verb = VerbFor(operation);
        CancelSemantics = CancelSemanticsFor(operation);
        IsModal = isModal;
        IsRunning = true;
        IsIndeterminate = true;
        IsCancellable = operation != VaultOperation.KeyDerivation;
        CancelRequested = false;
        WasCancelled = false;
        CurrentItem = null;
        ItemsDone = 0;
        ItemsTotal = 0;
        BytesDone = 0;
        BytesTotal = 0;
        Percent = 0;
        Throughput = null;
        Eta = null;
        _clock.Restart();
        CancelCommand.NotifyCanExecuteChanged();
        Started?.Invoke(this, EventArgs.Empty);
    }

    private void End()
    {
        _clock.Stop();
        IsRunning = false;
        IsCancellable = false;
        CancelCommand.NotifyCanExecuteChanged();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(VaultProgress progress)
    {
        CurrentItem = progress.CurrentItem;
        ItemsDone = progress.ItemsDone;
        ItemsTotal = progress.ItemsTotal;
        BytesDone = progress.BytesDone;
        BytesTotal = progress.BytesTotal;

        bool wasCancellable = IsCancellable;
        IsCancellable = progress.IsCancellable && !CancelRequested;
        if (wasCancellable != IsCancellable)
        {
            CancelCommand.NotifyCanExecuteChanged();
        }

        if (progress.BytesTotal > 0)
        {
            IsIndeterminate = false;
            Percent = Math.Clamp(progress.BytesDone * 100.0 / progress.BytesTotal, 0, 100);
        }
        else if (progress.ItemsTotal > 0)
        {
            IsIndeterminate = false;
            Percent = Math.Clamp(progress.ItemsDone * 100.0 / progress.ItemsTotal, 0, 100);
        }
        else
        {
            IsIndeterminate = true;
            Percent = 0;
        }

        TimeSpan elapsed = _clock.Elapsed;
        if (progress.BytesDone > 0 && elapsed > TimeSpan.FromMilliseconds(250))
        {
            double bytesPerSecond = progress.BytesDone / elapsed.TotalSeconds;
            Throughput = $"{bytesPerSecond / (1024 * 1024):N1} MB/s";

            if (elapsed >= EtaDelay && progress.BytesTotal > progress.BytesDone)
            {
                double secondsLeft = (progress.BytesTotal - progress.BytesDone) / bytesPerSecond;
                Eta = FormatEta(TimeSpan.FromSeconds(secondsLeft));
            }
        }
    }

    partial void OnIsRunningChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    partial void OnCancelRequestedChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
}
