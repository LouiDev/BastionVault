using System.Globalization;
using Bastion.Core;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>One label-and-value row of the Properties dialog.</summary>
/// <param name="Label">What the value is.</param>
/// <param name="Value">The value, already formatted.</param>
/// <param name="IsMono">True when the value is a cryptographic quantity and must be set in Text.Mono.</param>
public sealed record PropertyRow(string Label, string Value, bool IsMono = false);

/// <summary>One section of the Properties dialog.</summary>
/// <param name="Heading">Rule-and-caps heading.</param>
/// <param name="Rows">The rows under it.</param>
public sealed record PropertySection(string Heading, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// Properties of an entry or of the whole vault (UI-CONTRACT.md section 7). Every cryptographic
/// quantity - KDF parameters, save counter, on-disk size - is set in the mono face, because those
/// are numbers a user compares rather than reads.
/// </summary>
public sealed class PropertiesDialogViewModel : DialogViewModelBase<bool>
{
    /// <summary>Creates the vault flavour.</summary>
    /// <param name="vaultPath">Full path of the vault file.</param>
    /// <param name="statistics">Aggregate numbers from the session.</param>
    /// <param name="kdf">Argon2id parameters stored in the header.</param>
    /// <param name="pending">What a save would commit.</param>
    /// <param name="sizeObfuscation">True when size obfuscation is on for this vault.</param>
    /// <param name="reencryptOnSave">True when every save re-encrypts.</param>
    public PropertiesDialogViewModel(
        string vaultPath,
        VaultStatistics statistics,
        KdfParameters kdf,
        PendingChanges pending,
        bool sizeObfuscation,
        bool reencryptOnSave)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(kdf);
        ArgumentNullException.ThrowIfNull(pending);

        Title = "Vault properties";
        Sections =
        [
            new PropertySection("File",
            [
                new PropertyRow("Path", vaultPath),
                new PropertyRow("On disk", OperationViewModel.FormatBytes(statistics.OnDiskBytes), true),
                new PropertyRow("Opened from", statistics.OpenedFromIndexCopy ? "the backup index (save to repair)" : "the primary index"),
            ]),
            new PropertySection("Contents",
            [
                new PropertyRow("Folders", statistics.FolderCount.ToString("N0", CultureInfo.CurrentCulture)),
                new PropertyRow("Files", statistics.FileCount.ToString("N0", CultureInfo.CurrentCulture)),
                new PropertyRow("Plaintext", OperationViewModel.FormatBytes(statistics.TotalPlaintextBytes), true),
            ]),
            new PropertySection("Key derivation",
            [
                new PropertyRow("Algorithm", "Argon2id (RFC 9106), version 0x13", true),
                new PropertyRow("Memory", $"{kdf.MemoryKiB / 1024} MiB", true),
                new PropertyRow("Passes", kdf.Iterations.ToString(CultureInfo.InvariantCulture), true),
                new PropertyRow("Lanes", kdf.Parallelism.ToString(CultureInfo.InvariantCulture), true),
                new PropertyRow("Preset", kdf.MatchingPreset?.ToString() ?? "custom"),
            ]),
            new PropertySection("History",
            [
                new PropertyRow("Save counter", $"#{statistics.SaveCounter}", true),
                new PropertyRow("Last saved", statistics.LastSavedUtc is { } saved
                    ? saved.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    : "never"),
                new PropertyRow("Pending", pending.Any
                    ? $"{pending.Added} added · {pending.Changed} changed · {pending.Deleted} deleted"
                    : "nothing"),
            ]),
            new PropertySection("Write settings",
            [
                new PropertyRow("Size obfuscation", sizeObfuscation ? "on" : "off"),
                new PropertyRow("Re-encrypt on save", reencryptOnSave ? "on" : "off"),
            ]),
        ];
    }

    /// <summary>Creates the entry flavour.</summary>
    /// <param name="entry">The entry to describe.</param>
    /// <param name="vaultPath">In-vault path of the entry.</param>
    public PropertiesDialogViewModel(EntryInfo entry, string vaultPath)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Title = entry.Kind == EntryKind.Folder ? "Folder properties" : "File properties";
        Sections =
        [
            new PropertySection("Item",
            [
                new PropertyRow("Name", entry.Name),
                new PropertyRow("Location", vaultPath),
                new PropertyRow("Kind", entry.Kind == EntryKind.Folder ? "Folder" : "File"),
            ]),
            new PropertySection("Size",
            [
                new PropertyRow(entry.Kind == EntryKind.Folder ? "Contents" : "Size",
                    OperationViewModel.FormatBytes(entry.Length), true),
                new PropertyRow("Children", entry.Kind == EntryKind.Folder
                    ? entry.ChildCount.ToString("N0", CultureInfo.CurrentCulture)
                    : "-"),
            ]),
            new PropertySection("Timestamps",
            [
                new PropertyRow("Created", entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                new PropertyRow("Modified", entry.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
            ]),
            new PropertySection("State",
            [
                new PropertyRow("Since last save", entry.State switch
                {
                    EntryState.Added => "added",
                    EntryState.Changed => "changed",
                    _ => "unchanged",
                }),
                new PropertyRow("Comment", string.IsNullOrEmpty(entry.Comment) ? "-" : entry.Comment),
            ]),
        ];
    }

    /// <summary>The sections, in the order the dialog shows them.</summary>
    public IReadOnlyList<PropertySection> Sections { get; }
}
