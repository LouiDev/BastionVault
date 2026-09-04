using System.Globalization;
using Bastion.App.Controls;
using Bastion.App.Converters;
using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Tests.Explorer;

/// <summary>The explorer's converters. They are pure functions, so they are tested as such.</summary>
public sealed class ConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(2_411_008L, "2.3 MB")]
    public void ByteSizeFormatsTheWayEveryReadoutDoes(long bytes, string expected)
    {
        using var culture = new CultureScope(Invariant);

        Assert.Equal(expected, ByteSizeConverter.Instance.Convert(bytes, typeof(string), null, Invariant));
    }

    [Fact]
    public void AFolderRowShowsADashRatherThanARollup()
    {
        var folder = new EntryItemViewModel(Entry(EntryKind.Folder, "Docs", 4096), "\\Docs");

        Assert.Equal("-", ByteSizeConverter.Instance.Convert(folder, typeof(string), null, Invariant));
    }

    [Fact]
    public void AnUnknownValueConvertsToNothing()
    {
        Assert.Equal(string.Empty, ByteSizeConverter.Instance.Convert(new object(), typeof(string), null, Invariant));
    }

    [Fact]
    public void TodayShowsOnlyTheTime()
    {
        var now = new DateTimeOffset(2026, 5, 20, 14, 30, 0, TimeSpan.Zero);
        string text = RelativeDateConverter.Format(now.AddHours(-2), now, Invariant);

        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Yesterday", text, StringComparison.Ordinal);
    }

    [Fact]
    public void YesterdayIsNamed()
    {
        var now = new DateTimeOffset(2026, 5, 20, 14, 30, 0, TimeSpan.Zero);
        string text = RelativeDateConverter.Format(now.AddDays(-1), now, Invariant);

        Assert.StartsWith("Yesterday", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LastWeekShowsTheWeekday()
    {
        var now = new DateTimeOffset(2026, 5, 20, 14, 30, 0, TimeSpan.Zero);
        string text = RelativeDateConverter.Format(now.AddDays(-3), now, Invariant);

        Assert.Contains("Sun", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderYearKeepsTheYear()
    {
        var now = new DateTimeOffset(2026, 5, 20, 14, 30, 0, TimeSpan.Zero);
        string text = RelativeDateConverter.Format(now.AddYears(-2), now, Invariant);

        Assert.Contains("2024", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValueConvertsToAnEmptyDate()
    {
        Assert.Equal(string.Empty, RelativeDateConverter.Instance.Convert(null, typeof(string), null, Invariant));
    }

    [Theory]
    [InlineData(EntryState.Stored, PipState.None)]
    [InlineData(EntryState.Added, PipState.Added)]
    [InlineData(EntryState.Changed, PipState.Changed)]
    public void SaveStateBecomesAPip(EntryState state, PipState pip)
    {
        Assert.Equal(pip, StateToPipConverter.Instance.Convert(state, typeof(PipState), null, Invariant));
        Assert.Equal(pip, StateToPipConverter.PipFor(state));
    }

    [Fact]
    public void ARowConvertsToItsOwnPip()
    {
        var item = new EntryItemViewModel(Entry(EntryKind.File, "new.txt", 1, EntryState.Added), "\\new.txt");

        Assert.Equal(PipState.Added, StateToPipConverter.Instance.Convert(item, typeof(PipState), null, Invariant));
    }

    [Theory]
    [InlineData(EntryKind.Folder, false, "Glyph.Folder")]
    [InlineData(EntryKind.Folder, true, "Glyph.FolderOpen")]
    [InlineData(EntryKind.File, false, "Glyph.File")]
    public void KindPicksAGlyphKey(EntryKind kind, bool open, string key)
    {
        Assert.Equal(key, EntryKindToGlyphConverter.KeyFor(kind, open));
    }

    [Fact]
    public void AGlyphKeyResolvesToNothingWithoutAnApplication()
    {
        // The tests run without an Application, which is exactly the case the converter must not
        // throw in: a missing resource yields an empty glyph, never an exception.
        Assert.Equal(string.Empty, GlyphKeyConverter.Resolve("Glyph.Folder"));
        Assert.Equal(string.Empty, GlyphKeyConverter.Resolve(null));
    }

    [Theory]
    [InlineData("Compact", "Compact", true)]
    [InlineData("Compact", "compact", true)]
    [InlineData("Compact", "Spacious", false)]
    [InlineData("Compact", "Compact,Spacious", true)]
    [InlineData(null, "Compact", false)]
    public void EnumMatchComparesByName(string? value, string parameter, bool expected)
    {
        Assert.Equal(expected, EnumMatchConverter.Matches(value, parameter));
    }

    [Fact]
    public void TheHexDumpIsGroupedAndAnnotated()
    {
        byte[] bytes = [0x42, 0x61, 0x73, 0x74, 0x69, 0x6F, 0x6E, 0x00];

        string dump = PreviewViewModel.FormatHexDump(bytes, 1024);

        Assert.Contains("00000000", dump, StringComparison.Ordinal);
        Assert.Contains("42617374", dump, StringComparison.Ordinal);
        Assert.Contains("Bastion.", dump, StringComparison.Ordinal);
        Assert.Contains("more", dump, StringComparison.Ordinal);
    }

    /// <summary>Pins the thread's culture so a number format is asserted, not the machine's locale.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }

    private static EntryInfo Entry(EntryKind kind, string name, long length, EntryState state = EntryState.Stored) =>
        new(new EntryId(7), EntryId.Root, kind, name, length, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, string.Empty, state);
}
