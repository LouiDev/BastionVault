using Bastion.App.Services;
using Bastion.Core;

namespace Bastion.App.Tests.Explorer;

/// <summary>The natural order and the entry list's sort.</summary>
public sealed class SortingTests
{
    [Theory]
    [InlineData("file2", "file10")]
    [InlineData("file2.txt", "file10.txt")]
    [InlineData("2025-02 hosting.pdf", "2025-10 hosting.pdf")]
    [InlineData("IMG_9.jpg", "IMG_10.jpg")]
    [InlineData("a", "b")]
    [InlineData("Chapter 1", "Chapter 20")]
    public void NumbersInsideNamesCompareAsNumbers(string smaller, string larger)
    {
        Assert.True(NaturalStringComparer.Instance.Compare(smaller, larger) < 0, $"'{smaller}' should sort before '{larger}'");
        Assert.True(NaturalStringComparer.Instance.Compare(larger, smaller) > 0, $"'{larger}' should sort after '{smaller}'");
    }

    [Fact]
    public void TheComparerIsStableForEqualNames()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("same.txt", "same.txt"));
    }

    [Fact]
    public void NullsSortFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void AListSortsIntoExplorerOrder()
    {
        List<string> names = ["file10", "file2", "file1", "file20", "file3"];
        names.Sort(NaturalStringComparer.Instance);

        Assert.Equal(["file1", "file2", "file3", "file10", "file20"], names);
    }

    [Fact]
    public void TheManagedFallbackAgreesWithTheNativeOrder()
    {
        Assert.True(NaturalStringComparer.CompareManaged("file2", "file10") < 0);
        Assert.True(NaturalStringComparer.CompareManaged("file010", "file10") <= 0);
        Assert.True(NaturalStringComparer.CompareManaged("b", "A") > 0);
    }

    [Fact]
    public void FoldersComeFirstWhicheverWayTheSortRuns()
    {
        List<Sortable> entries =
        [
            new(EntryKind.File, "aaa.txt", 10, "Text document", Time(1)),
            new(EntryKind.Folder, "zzz", 0, "Folder", Time(2)),
        ];

        entries.Sort(new EntryComparer(EntrySortColumn.Name, ascending: true));
        Assert.Equal("zzz", entries[0].Name);

        entries.Sort(new EntryComparer(EntrySortColumn.Name, ascending: false));
        Assert.Equal("zzz", entries[0].Name);
    }

    [Fact]
    public void SizeSortsByBytesAndFallsBackToTheName()
    {
        List<Sortable> entries =
        [
            new(EntryKind.File, "b.txt", 100, "Text document", Time(1)),
            new(EntryKind.File, "a.txt", 100, "Text document", Time(2)),
            new(EntryKind.File, "big.bin", 5000, "File", Time(3)),
        ];

        entries.Sort(new EntryComparer(EntrySortColumn.Size, ascending: true));

        Assert.Equal(["a.txt", "b.txt", "big.bin"], entries.Select(e => e.Name));
    }

    [Fact]
    public void ModifiedSortsNewestLastWhenAscending()
    {
        List<Sortable> entries =
        [
            new(EntryKind.File, "new.txt", 1, "Text document", Time(30)),
            new(EntryKind.File, "old.txt", 1, "Text document", Time(1)),
        ];

        entries.Sort(new EntryComparer(EntrySortColumn.Modified, ascending: true));

        Assert.Equal(["old.txt", "new.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void ColumnKeysRoundTrip()
    {
        foreach (EntrySortColumn column in Enum.GetValues<EntrySortColumn>())
        {
            Assert.Equal(column, EntryComparer.ParseColumn(EntryComparer.KeyOf(column)));
        }

        Assert.Equal(EntrySortColumn.Name, EntryComparer.ParseColumn("nonsense"));
        Assert.Equal(EntrySortColumn.Name, EntryComparer.ParseColumn(null));
    }

    private static DateTimeOffset Time(int day) => new(2026, 1, day, 12, 0, 0, TimeSpan.Zero);

    private sealed record Sortable(EntryKind Kind, string Name, long Length, string TypeName, DateTimeOffset ModifiedUtc)
        : ISortableEntry;
}
