using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>
/// Cut, copy and paste inside the vault. Nothing here ever reaches the OS clipboard: the entries
/// are held as ids, and the ids are only meaningful together with the vault they came from
/// (UI-CONTRACT.md section 1.11).
/// </summary>
public sealed class InternalClipboard : IInternalClipboard
{
    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public ClipboardOp? Content { get; private set; }

    /// <inheritdoc />
    public void Set(IReadOnlyList<EntryId> ids, bool isCut, string sourceVaultPath)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(sourceVaultPath);

        Content = ids.Count == 0 ? null : new ClipboardOp([.. ids], isCut, sourceVaultPath);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (Content is null)
        {
            return;
        }

        Content = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
