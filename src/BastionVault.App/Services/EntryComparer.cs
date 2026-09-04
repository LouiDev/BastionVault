using System.Collections;
using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>Which column the entry list is ordered by.</summary>
public enum EntrySortColumn
{
    /// <summary>Natural order by name.</summary>
    Name,

    /// <summary>Plaintext bytes; a folder uses its recursive rollup.</summary>
    Size,

    /// <summary>The friendly type name from <see cref="FileTypeCatalog"/>.</summary>
    Type,

    /// <summary>Last modification time.</summary>
    Modified,
}

/// <summary>
/// The minimum an item has to expose to be sorted by <see cref="EntryComparer"/>. It keeps the
/// comparer out of the view models while still letting the list sort real rows.
/// </summary>
public interface ISortableEntry
{
    /// <summary>Folder or file.</summary>
    EntryKind Kind { get; }

    /// <summary>The entry name as the user sees it.</summary>
    string Name { get; }

    /// <summary>Plaintext bytes; a folder reports its recursive rollup.</summary>
    long Length { get; }

    /// <summary>Friendly type name shown in the Type column.</summary>
    string TypeName { get; }

    /// <summary>Last modification time.</summary>
    DateTimeOffset ModifiedUtc { get; }
}

/// <summary>
/// The entry list's sort (UI-CONTRACT.md section 1.5): folders always come before files whatever
/// the column and direction, and names use <see cref="NaturalStringComparer"/> so numbered files
/// land in the order a person would write them. Name is the tie-breaker of every other column, so
/// the order is stable and total.
/// </summary>
/// <param name="column">Column the list is ordered by.</param>
/// <param name="ascending">True for ascending.</param>
public sealed class EntryComparer(EntrySortColumn column, bool ascending) : IComparer<ISortableEntry>, IComparer
{
    /// <summary>Column the comparer orders by.</summary>
    public EntrySortColumn Column { get; } = column;

    /// <summary>True when the order is ascending.</summary>
    public bool Ascending { get; } = ascending;

    /// <summary>Maps a persisted column key to a sort column; unknown keys fall back to Name.</summary>
    /// <param name="key">Column key, for example "modified".</param>
    public static EntrySortColumn ParseColumn(string? key) => key?.ToUpperInvariant() switch
    {
        "SIZE" => EntrySortColumn.Size,
        "TYPE" => EntrySortColumn.Type,
        "MODIFIED" => EntrySortColumn.Modified,
        _ => EntrySortColumn.Name,
    };

    /// <summary>The persisted key of a sort column.</summary>
    /// <param name="column">The sort column.</param>
    public static string KeyOf(EntrySortColumn column) => column switch
    {
        EntrySortColumn.Size => "size",
        EntrySortColumn.Type => "type",
        EntrySortColumn.Modified => "modified",
        _ => "name",
    };

    /// <inheritdoc />
    public int Compare(ISortableEntry? x, ISortableEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        // Folders first, in both directions: a reversed sort should not bury the folders.
        int kind = Rank(x.Kind) - Rank(y.Kind);
        if (kind != 0)
        {
            return kind;
        }

        int result = Column switch
        {
            EntrySortColumn.Size => x.Length.CompareTo(y.Length),
            EntrySortColumn.Type => string.Compare(x.TypeName, y.TypeName, StringComparison.CurrentCultureIgnoreCase),
            EntrySortColumn.Modified => x.ModifiedUtc.CompareTo(y.ModifiedUtc),
            _ => 0,
        };

        // Name breaks every tie, so two files that agree on size or type still have one order.
        if (result == 0)
        {
            result = NaturalStringComparer.Instance.Compare(x.Name, y.Name);
        }

        return Ascending ? result : -result;
    }

    /// <inheritdoc />
    int IComparer.Compare(object? x, object? y) => Compare(x as ISortableEntry, y as ISortableEntry);

    private static int Rank(EntryKind kind) => kind == EntryKind.Folder ? 0 : 1;
}
