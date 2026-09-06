using BastionVault.App.Behaviors;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;
using BastionVault.App.Views;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The layout rules that were left open after 1.0 (DEVELOPING.md section 7, "Cosmetic and small"): side
/// panes that follow the window width (#18), a hex dump that fits the pane (#19), and a column order that
/// survives a restart (#23). Each rule is a pure function the view calls, so it is pinned here without a
/// window.
/// </summary>
public sealed class ResponsiveLayoutTests
{
    // ── #19 hex dump width ────────────────────────────────────────────────────

    [Fact]
    public void Sixteen_bytes_need_sixty_three_columns_and_eight_need_thirty_seven()
    {
        Assert.Equal(63, PreviewViewModel.HexLineColumns(16));
        Assert.Equal(37, PreviewViewModel.HexLineColumns(8));
    }

    [Theory]
    [InlineData(63, 16)]
    [InlineData(80, 16)]
    [InlineData(62, 8)]
    [InlineData(37, 8)]
    [InlineData(10, 8)]
    public void The_bytes_per_line_follow_the_columns_the_pane_can_show(int columns, int expected)
    {
        Assert.Equal(expected, PreviewViewModel.HexBytesPerLineFor(columns));
    }

    [Fact]
    public void An_eight_byte_line_keeps_the_ascii_column_and_the_offsets_advance_by_eight()
    {
        byte[] bytes = "Bastion Vault!!!"u8.ToArray();

        string dump = PreviewViewModel.FormatHexDump(bytes, bytes.Length, 8);
        string[] lines = dump.TrimEnd('\n').Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal("00000000  42617374 696F6E20  Bastion ", lines[0]);
        Assert.Equal("00000008  5661756C 74212121  Vault!!!", lines[1]);
        Assert.All(lines, line => Assert.Equal(PreviewViewModel.HexLineColumns(8), line.Length));
    }

    [Fact]
    public void A_sixteen_byte_line_is_the_familiar_layout()
    {
        byte[] bytes = "Bastion Vault!!!"u8.ToArray();

        string wide = PreviewViewModel.FormatHexDump(bytes, bytes.Length, 16);
        string implicitWidth = PreviewViewModel.FormatHexDump(bytes, bytes.Length);

        Assert.Equal(implicitWidth, wide);
        Assert.Equal(PreviewViewModel.HexLineColumns(16), wide.TrimEnd('\n').Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void Bytes_per_line_must_be_a_positive_multiple_of_four(int bytesPerLine)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewViewModel.FormatHexDump([1, 2, 3], 3, bytesPerLine));
    }

    // ── #23 column order ──────────────────────────────────────────────────────

    private static readonly string[] BuildOrder = ["name", "size", "type", "modified"];

    [Fact]
    public void A_persisted_order_is_applied()
    {
        List<ColumnState> saved =
        [
            new() { Key = "modified", Order = 0 },
            new() { Key = "name", Order = 1 },
            new() { Key = "type", Order = 2 },
            new() { Key = "size", Order = 3 },
        ];

        Assert.Equal(["modified", "name", "type", "size"], ColumnOrder.Resolve(BuildOrder, saved));
    }

    [Fact]
    public void A_column_the_build_no_longer_has_is_ignored_and_a_new_one_is_appended()
    {
        List<ColumnState> saved =
        [
            new() { Key = "size", Order = 0 },
            new() { Key = "attributes", Order = 1 },   // gone in this build
            new() { Key = "name", Order = 2 },
        ];

        // "type" and "modified" are not in the layout: they follow in build order.
        Assert.Equal(["size", "name", "type", "modified"], ColumnOrder.Resolve(BuildOrder, saved));
    }

    [Fact]
    public void An_empty_layout_keeps_the_build_order()
    {
        Assert.Equal(BuildOrder, ColumnOrder.Resolve(BuildOrder, []));
    }

    [Fact]
    public void Duplicate_positions_fall_back_to_the_build_order()
    {
        List<ColumnState> saved =
        [
            new() { Key = "type", Order = 0 },
            new() { Key = "name", Order = 0 },
            new() { Key = "size", Order = 0 },
        ];

        Assert.Equal(["name", "size", "type", "modified"], ColumnOrder.Resolve(BuildOrder, saved));
    }

    [Fact]
    public void Keys_are_matched_without_regard_to_case()
    {
        List<ColumnState> saved = [new() { Key = "MODIFIED", Order = 0 }, new() { Key = "Name", Order = 1 }];

        Assert.Equal(["modified", "name", "size", "type"], ColumnOrder.Resolve(BuildOrder, saved));
    }

    [Fact]
    public void The_order_round_trips_through_the_settings_copy()
    {
        var settings = new AppSettings();
        settings.ColumnLayout.Columns.Add(new ColumnState { Key = "type", Width = 100, Order = 0 });
        settings.ColumnLayout.Columns.Add(new ColumnState { Key = "name", Width = 200, Order = 1 });

        AppSettings copy = settings.Clone();

        Assert.Equal(
            ColumnOrder.Resolve(BuildOrder, settings.ColumnLayout.Columns),
            ColumnOrder.Resolve(BuildOrder, copy.ColumnLayout.Columns));
        Assert.Equal(["type", "name", "size", "modified"], ColumnOrder.Resolve(BuildOrder, copy.ColumnLayout.Columns));
    }
}
