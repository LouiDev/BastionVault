using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;
using BastionVault.Core;

namespace BastionVault.App.Behaviors;

/// <summary>
/// The inline rename editor. F2 (or the Rename command) swaps a row's name label for a text box,
/// preselects the stem without the extension, commits on Enter or focus loss and reverts on
/// Escape. A name Core refuses does not close the editor: the reason appears in the tooltip and
/// the text stays there to be fixed.
/// </summary>
public static class InlineRenameBehavior
{
    /// <summary>Name of the label inside the Name cell template.</summary>
    public const string LabelName = "PART_Name";

    /// <summary>Name of the editor inside the Name cell template.</summary>
    public const string EditorName = "PART_Rename";

    /// <summary>Identifies the <c>InlineRenameBehavior.Explorer</c> attached property.</summary>
    public static readonly DependencyProperty ExplorerProperty = DependencyProperty.RegisterAttached(
        "Explorer", typeof(ExplorerViewModel), typeof(InlineRenameBehavior),
        new PropertyMetadata(null, OnExplorerChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(RenameState), typeof(InlineRenameBehavior), new PropertyMetadata(null));

    /// <summary>Reads the explorer the editor renames for.</summary>
    /// <param name="element">The list.</param>
    public static ExplorerViewModel? GetExplorer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ExplorerViewModel?)element.GetValue(ExplorerProperty);
    }

    /// <summary>Wires a list up for inline renaming.</summary>
    /// <param name="element">The list.</param>
    /// <param name="value">The explorer, or <see langword="null"/> to detach.</param>
    public static void SetExplorer(DependencyObject element, ExplorerViewModel? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplorerProperty, value);
    }

    /// <summary>True while a row of this list is being renamed.</summary>
    /// <param name="element">The list.</param>
    public static bool IsEditing(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(StateProperty) is RenameState { IsEditing: true };
    }

    private static void OnExplorerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        if (list.GetValue(StateProperty) is RenameState previous)
        {
            previous.Detach();
            list.SetValue(StateProperty, null);
        }

        if (e.NewValue is not ExplorerViewModel explorer)
        {
            return;
        }

        var state = new RenameState(list, explorer);
        list.SetValue(StateProperty, state);
        state.Attach();
    }

    private sealed class RenameState(ListView list, ExplorerViewModel explorer)
    {
        private EntryItemViewModel? _item;
        private TextBox? _editor;
        private TextBlock? _label;
        private bool _closing;

        public bool IsEditing => _editor is not null;

        public void Attach() => explorer.RenameRequested += OnRenameRequested;

        public void Detach()
        {
            explorer.RenameRequested -= OnRenameRequested;
            EndEdit();
        }

        private void OnRenameRequested(object? sender, EntryItemViewModel item) => BeginEdit(item, item.RealName, null);

        private void BeginEdit(EntryItemViewModel item, string text, string? problem)
        {
            if (IsEditing)
            {
                EndEdit();
            }

            list.ScrollIntoView(item);
            list.UpdateLayout();

            if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem container)
            {
                return;
            }

            TextBox? editor = VisualSearch.Descendant<TextBox>(container, EditorName);
            TextBlock? label = VisualSearch.Descendant<TextBlock>(container, LabelName);
            if (editor is null)
            {
                return;
            }

            _item = item;
            _editor = editor;
            _label = label;

            editor.Text = text;
            editor.ToolTip = problem;
            editor.Visibility = Visibility.Visible;
            if (label is not null)
            {
                label.Visibility = Visibility.Collapsed;
            }

            editor.PreviewKeyDown += OnEditorKeyDown;
            editor.LostKeyboardFocus += OnEditorLostFocus;

            editor.Focus();
            Keyboard.Focus(editor);
            int stem = FileTypeCatalog.StemLength(text);
            editor.Select(0, stem);
        }

        private void EndEdit()
        {
            if (_editor is null)
            {
                return;
            }

            _editor.PreviewKeyDown -= OnEditorKeyDown;
            _editor.LostKeyboardFocus -= OnEditorLostFocus;
            _editor.Visibility = Visibility.Collapsed;
            _editor.ToolTip = null;

            if (_label is not null)
            {
                _label.Visibility = Visibility.Visible;
            }

            _editor = null;
            _label = null;
            _item = null;
        }

        private void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    _ = CommitAsync();
                    break;

                case Key.Escape:
                    e.Handled = true;
                    Cancel();
                    break;

                default:
                    break;
            }
        }

        private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_closing)
            {
                _ = CommitAsync();
            }
        }

        private void Cancel()
        {
            EndEdit();
            list.Focus();
        }

        private async Task CommitAsync()
        {
            if (_closing || _editor is null || _item is null)
            {
                return;
            }

            _closing = true;
            EntryItemViewModel item = _item;
            string text = _editor.Text;

            try
            {
                EndEdit();
                list.Focus();
            }
            finally
            {
                _closing = false;
            }

            NameCheck check = await explorer.CommitRenameAsync(item, text).ConfigureAwait(true);
            if (check.IsValid)
            {
                return;
            }

            // Put the editor back with what was typed, so the user fixes rather than retypes.
            EntryItemViewModel? again = explorer.Items.FirstOrDefault(i => i.Id == item.Id) ?? item;
            BeginEdit(again, check.Suggestion ?? text, check.Reason);
        }
    }
}
