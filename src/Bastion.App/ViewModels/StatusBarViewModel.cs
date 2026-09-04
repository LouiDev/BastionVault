using System.Globalization;
using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels;

/// <summary>
/// The explorer's status bar: how much is in the folder, how much of it is selected, the vault's
/// key-derivation parameters as an instrument readout, a pending-changes chip that opens the
/// popover, and - while something long is running - inline progress with a Cancel button
/// (UI-CONTRACT.md sections 2 and 4).
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    private readonly IVaultSession _session;

    [ObservableProperty]
    private string _itemsLine = string.Empty;

    [ObservableProperty]
    private string _selectionLine = string.Empty;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private PendingChanges _pending = new(0, 0, 0, 0, false, false);

    [ObservableProperty]
    private bool _isPendingPopoverOpen;

    [ObservableProperty]
    private string? _message;

    /// <summary>Creates the status bar over a session.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="operation">The shared long-operation runner, for inline progress.</param>
    /// <param name="undo">Undo, offered by the pending-changes popover.</param>
    public StatusBarViewModel(IVaultSession session, OperationViewModel operation, IAsyncRelayCommand undo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);

        _session = session;
        Operation = operation;
        PendingChangesPopover = new PendingChangesViewModel(session, undo);
        Update([], []);
    }

    /// <summary>The shared long-operation runner; the bar binds its progress and Cancel.</summary>
    public OperationViewModel Operation { get; }

    /// <summary>The popover behind the pending-changes chip.</summary>
    public PendingChangesViewModel PendingChangesPopover { get; }

    /// <summary>Argon2id parameters, set in the mono face like every cryptographic quantity.</summary>
    public string KdfLine
    {
        get
        {
            KdfParameters kdf = _session.Kdf;
            return string.Create(
                CultureInfo.CurrentCulture,
                $"Argon2id · {kdf.MemoryKiB / 1024} MiB · {kdf.Iterations} passes");
        }
    }

    /// <summary>True when a save would commit something.</summary>
    public bool HasPending => Pending.Any;

    /// <summary>"3 added · 1 changed · 2 deleted", short enough for a chip.</summary>
    public string PendingLine
    {
        get
        {
            var parts = new List<string>(3);
            if (Pending.Added > 0)
            {
                parts.Add($"{Pending.Added} added");
            }

            if (Pending.Changed > 0)
            {
                parts.Add($"{Pending.Changed} changed");
            }

            if (Pending.Deleted > 0)
            {
                parts.Add($"{Pending.Deleted} deleted");
            }

            return parts.Count == 0 ? "saved" : string.Join(" · ", parts);
        }
    }

    /// <summary>Re-reads the counts from the current folder listing and the selection.</summary>
    /// <param name="items">Everything the list is showing.</param>
    /// <param name="selection">The rows that are selected.</param>
    public void Update(IReadOnlyList<EntryItemViewModel> items, IReadOnlyList<EntryItemViewModel> selection)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selection);

        long total = 0;
        for (int i = 0; i < items.Count; i++)
        {
            total += items[i].Length;
        }

        ItemsLine = items.Count == 1
            ? $"1 item · {OperationViewModel.FormatBytes(total)}"
            : string.Create(CultureInfo.CurrentCulture, $"{items.Count:N0} items · {OperationViewModel.FormatBytes(total)}");

        HasSelection = selection.Count > 0;
        if (!HasSelection)
        {
            SelectionLine = string.Empty;
        }
        else
        {
            long selected = 0;
            for (int i = 0; i < selection.Count; i++)
            {
                selected += selection[i].Length;
            }

            SelectionLine = string.Create(
                CultureInfo.CurrentCulture,
                $"{selection.Count:N0} of {items.Count:N0} selected · {OperationViewModel.FormatBytes(selected)}");
        }

        RefreshVaultState();
    }

    /// <summary>Re-reads the pending counts and the KDF line after a change or a save.</summary>
    public void RefreshVaultState()
    {
        Pending = _session.Pending;
        PendingChangesPopover.Refresh();
        OnPropertyChanged(nameof(KdfLine));
    }

    /// <summary>Opens the pending-changes popover.</summary>
    [RelayCommand]
    public void ShowPendingChanges()
    {
        PendingChangesPopover.Refresh();
        IsPendingPopoverOpen = true;
    }

    partial void OnPendingChanged(PendingChanges value)
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingLine));
    }
}
