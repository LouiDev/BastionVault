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
    // ── #18 side panes ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1180)]
    [InlineData(1400)]
    [InlineData(2560)]
    public void Wide_windows_keep_the_widths_the_user_chose(double width)
    {
        (double tree, double preview, bool collapse) = ExplorerView.PaneWidthsFor(width, 300, 400);

        Assert.Equal(300, tree);
        Assert.Equal(400, preview);
        Assert.False(collapse);
    }

    [Fact]
    public void Between_the_breakpoints_both_panes_shrink_in_proportion()
    {
        double middle = (ExplorerView.PaneShrinkBreakpoint + ExplorerView.PreviewCollapseBreakpoint) / 2;

        (double tree, double preview, bool collapse) = ExplorerView.PaneWidthsFor(
            middle, ExplorerView.DefaultTreeWidth, ExplorerView.DefaultPreviewWidth);

        Assert.Equal((ExplorerView.TreeMinWidth + ExplorerView.DefaultTreeWidth) / 2, tree, 0.01);
        Assert.Equal((ExplorerView.PreviewMinWidth + ExplorerView.DefaultPreviewWidth) / 2, preview, 0.01);
        Assert.False(collapse);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(880)]
    [InlineData(700)]
    public void Narrow_windows_fold_the_preview_away_and_put_the_tree_at_its_minimum(double width)
    {
        (double tree, double preview, bool collapse) = ExplorerView.PaneWidthsFor(
            width, ExplorerView.DefaultTreeWidth, ExplorerView.DefaultPreviewWidth);

        Assert.Equal(ExplorerView.TreeMinWidth, tree);
        Assert.Equal(ExplorerView.PreviewMinWidth, preview);
        Assert.True(collapse);
    }

    [Fact]
    public void At_the_declared_minimum_window_width_the_list_gets_the_room_its_columns_need()
    {
        // 880 is the shell's minimum width. Tree at its minimum plus two 1 px seams and no preview leaves
        // the four default columns (24 + 226 + 80 + 124 + 124 = 578) their room with margin to spare.
        (double tree, _, bool collapse) = ExplorerView.PaneWidthsFor(880, ExplorerView.DefaultTreeWidth, ExplorerView.DefaultPreviewWidth);

        double list = 880 - tree - 2;
        Assert.True(collapse);
        Assert.True(list >= 578 + 22, $"the list gets {list} px, the four columns need 600");
    }

    [Fact]
    public void A_user_width_below_the_minimum_is_raised_to_it()
    {
        (double tree, double preview, _) = ExplorerView.PaneWidthsFor(1400, 40, 40);

        Assert.Equal(ExplorerView.TreeMinWidth, tree);
        Assert.Equal(ExplorerView.PreviewMinWidth, preview);
    }

    [Fact]
    public void The_view_model_hides_the_preview_while_the_window_is_narrow_without_forgetting_the_choice()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;
        Assert.True(explorer.IsPreviewVisible);
        Assert.True(explorer.IsPreviewShown);

        explorer.IsPreviewCollapsedByWidth = true;

        Assert.True(explorer.IsPreviewVisible, "the remembered choice is untouched");
        Assert.False(explorer.IsPreviewShown);
        Assert.False(explorer.Preview.IsEnabled, "a pane that is not on screen decrypts nothing");
        Assert.True(context.Settings.Current.PreviewEnabled, "nothing was persisted");

        explorer.IsPreviewCollapsedByWidth = false;

        Assert.True(explorer.IsPreviewShown);
        Assert.True(explorer.Preview.IsEnabled);
    }

    [Fact]
    public void Toggling_the_preview_while_it_is_folded_away_brings_it_back_instead_of_turning_it_off()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;
        explorer.IsPreviewCollapsedByWidth = true;

        explorer.TogglePreviewCommand.Execute(null);

        Assert.True(explorer.IsPreviewVisible);
        Assert.False(explorer.IsPreviewCollapsedByWidth);
        Assert.True(explorer.IsPreviewShown);

        // The second press is the ordinary toggle and is remembered.
        explorer.TogglePreviewCommand.Execute(null);

        Assert.False(explorer.IsPreviewVisible);
        Assert.False(context.Settings.Current.PreviewEnabled);
    }

    [Fact]
    public void A_preview_the_user_turned_off_stays_off_whatever_the_width_does()
    {
        using var context = new ExplorerTestContext();
        ExplorerViewModel explorer = context.Explorer;
        explorer.TogglePreviewCommand.Execute(null);
        Assert.False(explorer.IsPreviewVisible);

        explorer.IsPreviewCollapsedByWidth = true;
        Assert.False(explorer.IsPreviewShown);
        explorer.IsPreviewCollapsedByWidth = false;
        Assert.False(explorer.IsPreviewShown);
        Assert.False(context.Settings.Current.PreviewEnabled);
    }

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
