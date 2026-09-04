using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Wires the grid view's headers to the explorer's sort: a click sorts (and a second click on the
/// same column reverses), the header shows the chevron through its <c>Tag</c>, the list's
/// <c>CustomSort</c> is kept in step with an <see cref="EntryComparer"/>, and column widths and the
/// sort survive a restart through <see cref="ISettingsService"/> (UI-CONTRACT.md section 1.5).
/// </summary>
public static class ColumnSortBehavior
{
    /// <summary>Identifies the <c>ColumnSortBehavior.Explorer</c> attached property.</summary>
    public static readonly DependencyProperty ExplorerProperty = DependencyProperty.RegisterAttached(
        "Explorer", typeof(ExplorerViewModel), typeof(ColumnSortBehavior),
        new PropertyMetadata(null, OnExplorerChanged));

    /// <summary>Identifies the <c>ColumnSortBehavior.SortKey</c> attached property.</summary>
    public static readonly DependencyProperty SortKeyProperty = DependencyProperty.RegisterAttached(
        "SortKey", typeof(string), typeof(ColumnSortBehavior), new PropertyMetadata(null));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(SortState), typeof(ColumnSortBehavior), new PropertyMetadata(null));

    /// <summary>Reads the explorer whose sort the headers drive.</summary>
    /// <param name="element">The list.</param>
    public static ExplorerViewModel? GetExplorer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ExplorerViewModel?)element.GetValue(ExplorerProperty);
    }

    /// <summary>Wires a list's headers to an explorer's sort.</summary>
    /// <param name="element">The list.</param>
    /// <param name="value">The explorer, or <see langword="null"/> to detach.</param>
    public static void SetExplorer(DependencyObject element, ExplorerViewModel? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplorerProperty, value);
    }

    /// <summary>Reads the persisted key of a column ("name", "size", "type", "modified").</summary>
    /// <param name="element">The grid view column.</param>
    public static string? GetSortKey(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(SortKeyProperty);
    }

    /// <summary>Gives a column its persisted key; a column with no key is not sortable.</summary>
    /// <param name="element">The grid view column.</param>
    /// <param name="value">The key.</param>
    public static void SetSortKey(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SortKeyProperty, value);
    }

    private static void OnExplorerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        if (list.GetValue(StateProperty) is SortState previous)
        {
            previous.Detach();
            list.SetValue(StateProperty, null);
        }

        if (e.NewValue is not ExplorerViewModel explorer)
        {
            return;
        }

        var state = new SortState(list, explorer);
        list.SetValue(StateProperty, state);
        state.Attach();
    }

    private sealed class SortState(ListView list, ExplorerViewModel explorer)
    {
        /// <summary>The Name column never shrinks below this, however narrow the pane is.</summary>
        private const double MinimumNameWidth = 120;

        /// <summary>Border, cell padding and a possible vertical scrollbar.</summary>
        private const double ViewportChrome = 22;

        private readonly DependencyPropertyDescriptor? _itemsSource =
            DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListView));

        /// <summary>The width the user chose for Name; what is drawn may be narrower.</summary>
        private double _preferredNameWidth = double.NaN;

        public void Attach()
        {
            list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
            list.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler(OnGripperReleased));
            list.Unloaded += OnUnloaded;
            list.SizeChanged += OnListSizeChanged;
            explorer.PropertyChanged += OnExplorerPropertyChanged;
            _itemsSource?.AddValueChanged(list, OnItemsSourceChanged);

            ApplyPersistedWidths();
            FitNameColumn();
            Apply();
        }

        public void Detach()
        {
            SaveWidths();
            list.RemoveHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
            list.RemoveHandler(Thumb.DragCompletedEvent, new RoutedEventHandler(OnGripperReleased));
            list.Unloaded -= OnUnloaded;
            list.SizeChanged -= OnListSizeChanged;
            explorer.PropertyChanged -= OnExplorerPropertyChanged;
            _itemsSource?.RemoveValueChanged(list, OnItemsSourceChanged);
        }

        private static IEnumerable<GridViewColumn> Columns(ListView list) =>
            list.View is GridView view ? view.Columns : [];

        private void OnUnloaded(object? sender, RoutedEventArgs e) => SaveWidths();

        private void OnItemsSourceChanged(object? sender, EventArgs e) => Apply();

        private void OnExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ExplorerViewModel.SortColumn) or nameof(ExplorerViewModel.SortAscending))
            {
                Apply();
            }
        }

        private void OnHeaderClick(object? sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } header
                || header.Role == GridViewColumnHeaderRole.Padding)
            {
                return;
            }

            if (GetSortKey(column) is { Length: > 0 } key)
            {
                explorer.SortByCommand.Execute(key);
            }
        }

        private void OnGripperReleased(object? sender, RoutedEventArgs e)
        {
            // A drag on the Name gripper is the user stating a preference; anything narrower than
            // that is only what the current pane width allows.
            if (NameColumn is { } name && !double.IsNaN(name.Width))
            {
                _preferredNameWidth = name.Width;
            }

            SaveWidths();
            FitNameColumn();
        }

        private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                FitNameColumn();
            }
        }

        private GridViewColumn? NameColumn
        {
            get
            {
                foreach (GridViewColumn column in Columns(list))
                {
                    if (string.Equals(GetSortKey(column), "name", StringComparison.OrdinalIgnoreCase))
                    {
                        return column;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Gives Name whatever the fixed metadata columns leave over, so the column set fits the
        /// viewport instead of overflowing it. A GridView clips at the viewport, not at the
        /// column boundary, so an overflowing set cut Type mid-glyph with no ellipsis and pushed
        /// Modified off-screen entirely behind a thin horizontal scrollbar.
        /// </summary>
        private void FitNameColumn()
        {
            if (NameColumn is not { } name)
            {
                return;
            }

            if (double.IsNaN(_preferredNameWidth))
            {
                _preferredNameWidth = double.IsNaN(name.Width) ? name.ActualWidth : name.Width;
            }

            double available = list.ActualWidth - ViewportChrome;
            if (available <= 0)
            {
                return;
            }

            double others = 0;
            foreach (GridViewColumn column in Columns(list))
            {
                if (!ReferenceEquals(column, name))
                {
                    others += double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
                }
            }

            double ceiling = Math.Max(MinimumNameWidth, _preferredNameWidth);
            double target = Math.Clamp(available - others, MinimumNameWidth, ceiling);

            if (double.IsNaN(name.Width) || Math.Abs(name.Width - target) > 0.5)
            {
                name.Width = target;
            }
        }

        private void Apply()
        {
            var comparer = new EntryComparer(explorer.SortColumn, explorer.SortAscending);

            if (list.ItemsSource is not null
                && CollectionViewSource.GetDefaultView(list.ItemsSource) is ListCollectionView view)
            {
                // The explorer has already ordered the items; this keeps the view's own idea of
                // the order in step, so an in-place edit lands in the right row.
                view.CustomSort = comparer;
            }

            string active = EntryComparer.KeyOf(explorer.SortColumn);
            string direction = explorer.SortAscending ? "Ascending" : "Descending";

            foreach (GridViewColumnHeader header in VisualSearch.Descendants<GridViewColumnHeader>(list))
            {
                if (header.Column is not { } column)
                {
                    continue;
                }

                string? key = GetSortKey(column);
                header.Tag = key is not null && string.Equals(key, active, StringComparison.OrdinalIgnoreCase)
                    ? direction
                    : null;
            }
        }

        private void ApplyPersistedWidths()
        {
            List<ColumnState> saved = explorer.Settings.Current.ColumnLayout.Columns;
            if (saved.Count == 0)
            {
                return;
            }

            foreach (GridViewColumn column in Columns(list))
            {
                if (GetSortKey(column) is not { Length: > 0 } key)
                {
                    continue;
                }

                ColumnState? state = saved.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                if (state is { Width: >= 40 and <= 1200 })
                {
                    column.Width = state.Width;
                }
            }
        }

        private void SaveWidths()
        {
            ColumnLayout layout = explorer.Settings.Current.ColumnLayout;
            bool changed = false;
            int order = 0;

            foreach (GridViewColumn column in Columns(list))
            {
                if (GetSortKey(column) is not { Length: > 0 } key)
                {
                    continue;
                }

                // Name is persisted at the width the user chose, not at the width the current
                // pane happened to allow.
                double width = ReferenceEquals(column, NameColumn) && !double.IsNaN(_preferredNameWidth)
                    ? _preferredNameWidth
                    : double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
                if (width < 1)
                {
                    order++;
                    continue;
                }

                ColumnState? state = layout.Columns.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                if (state is null)
                {
                    layout.Columns.Add(new ColumnState { Key = key, Width = width, Order = order });
                    changed = true;
                }
                else if (Math.Abs(state.Width - width) > 0.5 || state.Order != order)
                {
                    state.Width = width;
                    state.Order = order;
                    changed = true;
                }

                order++;
            }

            if (changed)
            {
                explorer.Settings.Save();
            }
        }
    }
}
