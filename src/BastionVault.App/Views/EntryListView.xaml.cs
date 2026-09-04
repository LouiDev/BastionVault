using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BastionVault.App.Behaviors;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Views;

/// <summary>
/// The entry list. It is a virtualised <c>ListView</c> over a <c>GridView</c> whose columns all
/// have explicit widths, sorted through <c>CustomSort</c> and never rebuilt row by row: the view
/// model replaces the whole list at once (UI-CONTRACT.md section 1.5).
/// </summary>
public partial class EntryListView : UserControl
{
    private ExplorerViewModel? _explorer;
    private bool _syncingSelection;

    /// <summary>Creates the list.</summary>
    public EntryListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>Moves keyboard focus into the list, on the focused row when there is one.</summary>
    public void FocusList()
    {
        if (List.Items.Count == 0)
        {
            List.Focus();
            return;
        }

        object target = List.SelectedItem ?? List.Items[0]!;
        List.ScrollIntoView(target);

        if (List.ItemContainerGenerator.ContainerFromItem(target) is ListViewItem container)
        {
            container.Focus();
            return;
        }

        List.Focus();
    }

    /// <summary>Opens the context menu at the focused row; Shift+F10 and the Menu key land here.</summary>
    public void OpenContextMenuAtFocus()
    {
        object? target = List.SelectedItem ?? (List.Items.Count > 0 ? List.Items[0] : null);
        if (target is null)
        {
            if (List.ContextMenu is { } blank)
            {
                blank.PlacementTarget = List;
                blank.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
                blank.IsOpen = true;
            }

            return;
        }

        List.ScrollIntoView(target);
        if (List.ItemContainerGenerator.ContainerFromItem(target) is not ListViewItem container
            || container.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = container;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (DataContext is not ExplorerViewModel explorer)
        {
            return;
        }

        _explorer = explorer;
        explorer.SelectAllRequested += OnSelectAllRequested;
        explorer.ListFocusRequested += OnListFocusRequested;
        explorer.ContextMenuRequested += OnContextMenuRequested;
        explorer.SelectionRestored += OnSelectionRestored;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_explorer is null)
        {
            return;
        }

        _explorer.SelectAllRequested -= OnSelectAllRequested;
        _explorer.ListFocusRequested -= OnListFocusRequested;
        _explorer.ContextMenuRequested -= OnContextMenuRequested;
        _explorer.SelectionRestored -= OnSelectionRestored;
        _explorer = null;
    }

    private void OnSelectAllRequested(object? sender, EventArgs e)
    {
        List.SelectAll();
        FocusList();
    }

    private void OnListFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, FocusList);

    private void OnContextMenuRequested(object? sender, EventArgs e) => OpenContextMenuAtFocus();

    /// <summary>
    /// Puts a selection the view model restored back into the control. Assigning ItemsSource
    /// clears <c>ListView.SelectedItems</c> synchronously, so without this the rows the view
    /// model still considers selected are not highlighted and Delete acts on invisible rows.
    /// </summary>
    private void OnSelectionRestored(object? sender, IReadOnlyList<EntryItemViewModel> selection)
    {
        if (_syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (EntryItemViewModel row in selection)
            {
                if (List.Items.Contains(row))
                {
                    List.SelectedItems.Add(row);
                }
            }

            if (List.SelectedItems.Count > 0)
            {
                List.ScrollIntoView(List.SelectedItems[List.SelectedItems.Count - 1]!);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _explorer is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            _explorer.SetSelection([.. List.SelectedItems.OfType<EntryItemViewModel>()]);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_explorer is null || VisualSearch.Ancestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        if (row.DataContext is EntryItemViewModel item)
        {
            _explorer.OpenEntryCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A right click on an unselected row selects it first, so the menu acts on what was clicked.
        if (VisualSearch.Ancestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        if (!row.IsSelected)
        {
            List.SelectedItems.Clear();
            row.IsSelected = true;
        }

        row.Focus();
    }
}
