using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>
/// The modal progress card. It is a view over <see cref="OperationViewModel"/>: the verb, the item
/// with a middle ellipsis, "n of N", bytes, throughput, an ETA that appears only after two
/// seconds, and one sentence saying exactly what a cancel would leave behind. Cancel disables
/// itself and relabels to "Finishing — can't cancel" the moment Core reports a non-cancellable
/// phase (UI-CONTRACT.md section 7).
/// </summary>
public sealed partial class ProgressDialogViewModel : DialogViewModelBase<bool>
{
    private readonly OperationViewModel _operation;

    /// <summary>Creates the card over a running operation.</summary>
    /// <param name="operation">The operation to display.</param>
    public ProgressDialogViewModel(OperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        _operation = operation;
        Title = operation.Title;
        IsBusy = true;
        IsCancellable = false;

        _operation.PropertyChanged += OnOperationChanged;
    }

    /// <summary>The operation the card is showing.</summary>
    public OperationViewModel Operation => _operation;

    /// <summary>Label of the cancel button; it changes when cancelling stops being possible.</summary>
    public string CancelLabel =>
        _operation.IsCancellable && !_operation.CancelRequested
            ? "Cancel"
            : _operation.CancelRequested ? "Cancelling..." : "Finishing - can't cancel";

    /// <summary>The current item with a middle ellipsis, so both ends stay readable.</summary>
    public string CurrentItemDisplay => MiddleEllipsis(_operation.CurrentItem, 64);

    /// <summary>"12 of 340", or an empty string while the total is unknown.</summary>
    public string CountDisplay =>
        _operation.ItemsTotal > 0 ? $"{_operation.ItemsDone:N0} of {_operation.ItemsTotal:N0}" : string.Empty;

    /// <summary>"18.2 MB of 1.4 GB", or an empty string while the total is unknown.</summary>
    public string BytesDisplay =>
        _operation.BytesTotal > 0
            ? $"{OperationViewModel.FormatBytes(_operation.BytesDone)} of {OperationViewModel.FormatBytes(_operation.BytesTotal)}"
            : _operation.BytesDone > 0 ? OperationViewModel.FormatBytes(_operation.BytesDone) : string.Empty;

    /// <summary>Shortens a path in the middle: "C:\Users\...\report.pdf".</summary>
    /// <param name="text">Text to shorten; <see langword="null"/> yields an empty string.</param>
    /// <param name="maxLength">Maximum number of characters to keep.</param>
    public static string MiddleEllipsis(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text ?? string.Empty;
        }

        if (maxLength <= 3)
        {
            return "...";
        }

        int keep = maxLength - 3;
        int head = (keep + 1) / 2;
        int tail = keep - head;
        return string.Concat(text.AsSpan(0, head), "...", text.AsSpan(text.Length - tail, tail));
    }

    /// <summary>Asks the running operation to stop.</summary>
    [RelayCommand]
    public void RequestCancel() => _operation.CancelCommand.Execute(null);

    /// <summary>Closes the card once the operation has finished.</summary>
    /// <param name="completed">True when the operation ran to completion.</param>
    public void Complete(bool completed)
    {
        _operation.PropertyChanged -= OnOperationChanged;
        IsBusy = false;
        Close(completed);
    }

    private void OnOperationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OperationViewModel.IsCancellable):
            case nameof(OperationViewModel.CancelRequested):
                OnPropertyChanged(nameof(CancelLabel));
                break;
            case nameof(OperationViewModel.CurrentItem):
                OnPropertyChanged(nameof(CurrentItemDisplay));
                break;
            case nameof(OperationViewModel.ItemsDone):
            case nameof(OperationViewModel.ItemsTotal):
                OnPropertyChanged(nameof(CountDisplay));
                break;
            case nameof(OperationViewModel.BytesDone):
            case nameof(OperationViewModel.BytesTotal):
                OnPropertyChanged(nameof(BytesDisplay));
                break;
            case nameof(OperationViewModel.Title):
                Title = _operation.Title;
                break;
            default:
                break;
        }
    }
}
