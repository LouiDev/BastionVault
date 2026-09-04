namespace BastionVault.Core.Format;

/// <summary>
/// The deserialized index (FORMAT.md section 4.3): the complete tree plus the bookkeeping the data
/// section needs. Like <see cref="IndexEntry"/> this is the wire shape, not a domain object.
/// </summary>
public sealed class VaultIndex
{
    /// <summary>1 at creation, incremented by every successful save.</summary>
    public ulong SaveCounter;

    /// <summary>Time of the save as <see cref="DateTime"/> ticks (UTC).</summary>
    public long SavedUtcTicks;

    /// <summary>Total data section length: the sum of all blob lengths plus <see cref="DataPaddingLength"/>.</summary>
    public long DataSectionLength;

    /// <summary>Trailing CSPRNG bytes at the end of the data section; 0 unless size obfuscation is on.</summary>
    public long DataPaddingLength;

    /// <summary>Next id to allocate; greater than every id in <see cref="Entries"/>.</summary>
    public uint NextEntryId;

    /// <summary>The entries in canonical order (depth-first pre-order, children by ascending id).</summary>
    public List<IndexEntry> Entries = new();
}
