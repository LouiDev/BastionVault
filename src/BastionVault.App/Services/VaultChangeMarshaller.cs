using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>
/// Core raises <see cref="IVaultSession.Changed"/> on whatever thread did the work. This is the
/// one place that hops onto the UI thread, so no view model ever has to think about it
/// (UI-CONTRACT.md section 1.2).
/// </summary>
public sealed class VaultChangeMarshaller : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<VaultChangedEventArgs> _handler;
    private IVaultSession? _session;

    /// <summary>Creates a marshaller; it is idle until <see cref="Attach"/> is called.</summary>
    /// <param name="dispatcher">Marshals onto the UI thread.</param>
    /// <param name="handler">Called on the UI thread for every change.</param>
    public VaultChangeMarshaller(IUiDispatcher dispatcher, Action<VaultChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(handler);

        _dispatcher = dispatcher;
        _handler = handler;
    }

    /// <summary>Subscribes to a session, detaching from any previous one.</summary>
    /// <param name="session">Session to watch, or <see langword="null"/> to just detach.</param>
    public void Attach(IVaultSession? session)
    {
        Detach();

        _session = session;
        if (_session is not null)
        {
            _session.Changed += OnChanged;
        }
    }

    /// <summary>Unsubscribes from the current session.</summary>
    public void Detach()
    {
        if (_session is not null)
        {
            _session.Changed -= OnChanged;
            _session = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Detach();

    private void OnChanged(object? sender, VaultChangedEventArgs e) => _dispatcher.Post(() => _handler(e));
}
