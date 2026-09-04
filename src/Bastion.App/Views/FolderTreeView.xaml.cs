using System.Windows.Controls;
using System.Windows.Input;
using Bastion.App.Behaviors;
using Bastion.App.ViewModels;

namespace Bastion.App.Views;

/// <summary>
/// The folder tree. It is entirely view-model driven: expansion, selection and the pending dot are
/// bound two ways to <see cref="FolderNodeViewModel"/>, and nothing here reaches for a
/// <c>TreeViewItem</c> except to make a right click select the row it happened on, which WPF does
/// not do by itself.
/// </summary>
public partial class FolderTreeView : UserControl
{
    /// <summary>Creates the tree.</summary>
    public FolderTreeView()
    {
        InitializeComponent();
        Tree.PreviewMouseRightButtonDown += OnRightButtonDown;
    }

    /// <summary>Moves keyboard focus into the tree; F6 cycles here.</summary>
    public void FocusTree()
    {
        if (Tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
        {
            first.Focus();
            return;
        }

        Tree.Focus();
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select what was clicked before the menu opens, so the commands act on that folder.
        if (VisualSearch.Ancestor<TreeViewItem>(e.OriginalSource as System.Windows.DependencyObject) is not { } item)
        {
            return;
        }

        item.Focus();
        if (item.DataContext is FolderNodeViewModel node)
        {
            node.IsSelected = true;
        }
    }
}
