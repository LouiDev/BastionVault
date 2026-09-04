using System.Globalization;
using System.Windows;
using BastionVault.Core;

namespace BastionVault.App.Behaviors;

/// <summary>
/// The payload of an internal drag. Only ids travel, never view models or containers
/// (UI-CONTRACT.md section 1.7), and they travel as text so nothing has to be serialised by a
/// formatter: an id list is meaningless outside the vault it came from anyway, which is exactly
/// what makes a drag-out to Explorer safe to refuse.
/// </summary>
public static class VaultDragData
{
    /// <summary>Clipboard format name of an internal entry drag.</summary>
    public const string Format = "BastionVault.EntryIds";

    /// <summary>Clipboard format name carrying the vault the ids belong to.</summary>
    public const string VaultFormat = "BastionVault.Path";

    /// <summary>The text the adorner shows when the drag leaves the app.</summary>
    public const string RefusalText = "Use Export (Ctrl+Shift+E) to write files to disk.";

    /// <summary>Builds the data object for a drag of entries.</summary>
    /// <param name="ids">Entries being dragged.</param>
    /// <param name="vaultPath">Path of the vault they belong to.</param>
    public static DataObject Create(IReadOnlyList<EntryId> ids, string vaultPath)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var data = new DataObject();
        data.SetData(Format, string.Join(',', ids.Select(i => i.Value.ToString(CultureInfo.InvariantCulture))));
        data.SetData(VaultFormat, vaultPath ?? string.Empty);
        return data;
    }

    /// <summary>True when the data object carries an internal entry drag from this vault.</summary>
    /// <param name="data">The dragged data.</param>
    /// <param name="vaultPath">Path of the vault the drop target belongs to.</param>
    public static bool IsInternal(IDataObject? data, string vaultPath) =>
        data is not null
        && data.GetDataPresent(Format)
        && string.Equals(data.GetData(VaultFormat) as string, vaultPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the ids out of a data object; empty when it carries none.</summary>
    /// <param name="data">The dragged data.</param>
    public static IReadOnlyList<EntryId> Read(IDataObject? data)
    {
        if (data?.GetData(Format) is not string text || text.Length == 0)
        {
            return [];
        }

        var ids = new List<EntryId>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
            {
                ids.Add(new EntryId(value));
            }
        }

        return ids;
    }

    /// <summary>Reads a dropped Explorer file list, copied out of the event data.</summary>
    /// <param name="data">The dragged data.</param>
    public static IReadOnlyList<string> ReadFileDrop(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop))
        {
            return [];
        }

        // Copy the array out: the event data is only valid inside the OLE callback.
        return data.GetData(DataFormats.FileDrop) is string[] paths ? [.. paths] : [];
    }
}
