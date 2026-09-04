using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels.Dialogs;

/// <summary>
/// The non-generic face of a dialog, which is all the <c>DialogHost</c> needs: a title, whether
/// the dialog is busy, whether Escape may close it, and a notification when it is done.
/// </summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isCancellable = true;

    /// <summary>Raised exactly once, when the dialog has produced its result.</summary>
    public event EventHandler<object?>? Closed;

    /// <summary>True while Escape and the cancel button may dismiss the dialog.</summary>
    public bool CanClose => !IsBusy && IsCancellable;

    /// <summary>Dismisses the dialog with no result. Escape and the cancel button call this.</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (CanClose)
        {
            CloseWithDefault();
        }
    }

    /// <summary>
    /// Handles the Enter key. The default does nothing; a dialog whose primary action is
    /// unambiguous overrides it and returns true once it has acted.
    /// </summary>
    public virtual bool Accept() => false;

    /// <summary>Closes the dialog with the type's default result. Implemented by the generic base.</summary>
    protected abstract void CloseWithDefault();

    /// <summary>Raises <see cref="Closed"/> with the boxed result.</summary>
    /// <param name="result">The result the dialog produced.</param>
    protected void RaiseClosed(object? result) => Closed?.Invoke(this, result);

    partial void OnIsCancellableChanged(bool value) => OnPropertyChanged(nameof(CanClose));
}

/// <summary>
/// Base class of every in-window dialog. The result is delivered through a
/// <see cref="TaskCompletionSource{TResult}"/>, so <c>IDialogService.ShowAsync</c> is a plain await.
/// </summary>
/// <typeparam name="TResult">What the dialog produces; <see langword="default"/> means "cancelled".</typeparam>
public abstract class DialogViewModelBase<TResult> : DialogViewModelBase
{
    private readonly TaskCompletionSource<TResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _closed;

    /// <summary>Completes when the dialog closes.</summary>
    public Task<TResult?> Result => _completion.Task;

    /// <summary>Closes the dialog with a result. The second and later calls are ignored.</summary>
    /// <param name="result">The result; <see langword="default"/> for a cancellation.</param>
    public void Close(TResult? result)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        RaiseClosed(result);
        _completion.TrySetResult(result);
    }

    /// <inheritdoc />
    protected override void CloseWithDefault() => Close(default);
}
