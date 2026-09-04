using Bastion.App.Shell;
using Bastion.App.ViewModels.Dialogs;

namespace Bastion.App.Services;

/// <summary>
/// <see cref="IDialogService"/> over the shell's <see cref="DialogHost"/>. View models depend on
/// the interface and never learn that a dialog is a piece of the shell's visual tree.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly ILog _log;

    /// <summary>Creates the service; the host is attached once the shell window exists.</summary>
    /// <param name="log">Log.</param>
    public DialogService(ILog log) => _log = log;

    /// <summary>The host the shell window hands over on load.</summary>
    public DialogHost? Host { get; set; }

    /// <inheritdoc />
    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> dialog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        if (Host is null)
        {
            _log.Error("A dialog was requested before the shell window existed.");
            return Task.FromResult<TResult?>(default);
        }

        return Host.ShowAsync(dialog, ct);
    }

    /// <inheritdoc />
    public async Task<ConfirmResult> ConfirmAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new ConfirmDialogViewModel(request);
        return await ShowAsync(dialog).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task ShowErrorAsync(string title, string message, string? details = null)
    {
        var request = new ConfirmRequest(title, message, PrimaryVerb: "Close", CancelVerb: "Close", Detail: details);
        await ShowAsync(new ConfirmDialogViewModel(request, MessageKind.Error)).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async Task ShowInfoAsync(string title, string message)
    {
        var request = new ConfirmRequest(title, message, PrimaryVerb: "OK", CancelVerb: "OK");
        await ShowAsync(new ConfirmDialogViewModel(request, MessageKind.Information)).ConfigureAwait(true);
    }
}
