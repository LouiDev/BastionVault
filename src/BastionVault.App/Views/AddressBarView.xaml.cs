using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Views;

/// <summary>
/// The address bar: back, forward and up, then either the breadcrumb with its sibling dropdowns or
/// - on Ctrl+L, Alt+D, F4 or a click on the empty part of the row - an editable path box with
/// autocomplete. Escape puts the crumbs back; Enter resolves the path through Core.
/// </summary>
public partial class AddressBarView : UserControl
{
    private AddressBarViewModel? _addressBar;

    /// <summary>Creates the address bar.</summary>
    public AddressBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>Switches to the path box and puts the caret in it.</summary>
    public void FocusPathBox()
    {
        if (DataContext is ExplorerViewModel explorer)
        {
            explorer.AddressBar.BeginEdit();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (DataContext is ExplorerViewModel explorer)
        {
            _addressBar = explorer.AddressBar;
            _addressBar.EditRequested += OnEditRequested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_addressBar is not null)
        {
            _addressBar.EditRequested -= OnEditRequested;
            _addressBar = null;
        }
    }

    private void OnEditRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
        });

    private void OnCrumbRowClicked(object sender, MouseButtonEventArgs e)
    {
        // Only the empty part of the row switches to the path box; a crumb has its own button.
        if (e.OriginalSource == CrumbRow)
        {
            FocusPathBox();
            e.Handled = true;
        }
    }

    private void OnPathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ExplorerViewModel explorer)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (SuggestionList.SelectedItem is string picked)
                {
                    explorer.AddressBar.ApplySuggestion(picked);
                    SuggestionList.SelectedIndex = -1;
                    break;
                }

                if (explorer.AddressBar.TryCommit())
                {
                    Focus();
                }

                break;

            case Key.Escape:
                e.Handled = true;
                explorer.AddressBar.CancelEdit();
                Focus();
                break;

            case Key.Down:
                if (explorer.AddressBar.IsSuggestionListOpen && SuggestionList.Items.Count > 0)
                {
                    e.Handled = true;
                    SuggestionList.SelectedIndex = Math.Min(SuggestionList.SelectedIndex + 1, SuggestionList.Items.Count - 1);
                }

                break;

            case Key.Up:
                if (explorer.AddressBar.IsSuggestionListOpen && SuggestionList.SelectedIndex > 0)
                {
                    e.Handled = true;
                    SuggestionList.SelectedIndex--;
                }

                break;

            default:
                break;
        }
    }

    private void OnPathBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking a completion moves focus into the popup; that must not cancel the edit.
        if (e.NewFocus is DependencyObject next && Behaviors.VisualSearch.Ancestor<ListBox>(next) == SuggestionList)
        {
            return;
        }

        if (DataContext is ExplorerViewModel explorer && explorer.AddressBar.IsEditing)
        {
            explorer.AddressBar.CancelEdit();
        }
    }

    private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ExplorerViewModel explorer && SuggestionList.SelectedItem is string picked)
        {
            explorer.AddressBar.ApplySuggestion(picked);
            explorer.AddressBar.TryCommit();
        }
    }
}
