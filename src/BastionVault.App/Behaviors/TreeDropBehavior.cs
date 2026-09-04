using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BastionVault.App.ViewModels;
using BastionVault.Core;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Makes the folder tree a drop target: entries dragged from the list land in the folder under the
/// cursor, Explorer files dropped on a node are imported into it, a folder hovered for
/// <see cref="HoverExpandDelay"/> springs open so a deep drop needs no separate click, and a drop
/// into the dragged folder's own subtree is refused before Core is asked.
/// </summary>
public static class TreeDropBehavior
{
    /// <summary>How long a folder has to be hovered before it expands.</summary>
    public static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>Identifies the <c>TreeDropBehavior.Explorer</c> attached property.</summary>
    public static readonly DependencyProperty ExplorerProperty = DependencyProperty.RegisterAttached(
        "Explorer", typeof(ExplorerViewModel), typeof(TreeDropBehavior),
        new PropertyMetadata(null, OnExplorerChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DropState), typeof(TreeDropBehavior), new PropertyMetadata(null));

    /// <summary>Reads the explorer the tree drops into.</summary>
    /// <param name="element">The tree.</param>
    public static ExplorerViewModel? GetExplorer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ExplorerViewModel?)element.GetValue(ExplorerProperty);
    }

    /// <summary>Wires a tree up as a drop target.</summary>
    /// <param name="element">The tree.</param>
    /// <param name="value">The explorer, or <see langword="null"/> to detach.</param>
    public static void SetExplorer(DependencyObject element, ExplorerViewModel? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplorerProperty, value);
    }

    private static void OnExplorerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree)
        {
            return;
        }

        tree.DragOver -= OnDragOver;
        tree.DragLeave -= OnDragLeave;
        tree.Drop -= OnDrop;

        if (StateOf(tree) is { } previous)
        {
            previous.Dispose();
            tree.SetValue(StateProperty, null);
        }

        if (e.NewValue is not ExplorerViewModel)
        {
            return;
        }

        tree.SetValue(StateProperty, new DropState());
        tree.AllowDrop = true;
        tree.DragOver += OnDragOver;
        tree.DragLeave += OnDragLeave;
        tree.Drop += OnDrop;
    }

    private static DropState? StateOf(DependencyObject element) => (DropState?)element.GetValue(StateProperty);

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not TreeView tree || GetExplorer(tree) is not { } explorer || StateOf(tree) is not { } state)
        {
            return;
        }

        EdgeAutoScroll.Update(tree, e.GetPosition(tree));

        FolderNodeViewModel? node = NodeUnder(tree, e.GetPosition(tree));
        e.Effects = Resolve(explorer, e, node, out _);

        if (e.Effects == DragDropEffects.None)
        {
            state.Hover(null);
        }
        else
        {
            state.Hover(node);
        }

        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is TreeView tree)
        {
            StateOf(tree)?.Hover(null);
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not TreeView tree || GetExplorer(tree) is not { } explorer)
        {
            return;
        }

        FolderNodeViewModel? node = NodeUnder(tree, e.GetPosition(tree));
        DragDropEffects effects = Resolve(explorer, e, node, out EntryId target);

        StateOf(tree)?.Hover(null);
        e.Handled = true;

        if (effects == DragDropEffects.None)
        {
            return;
        }

        if (VaultDragData.IsInternal(e.Data, explorer.VaultPath))
        {
            IReadOnlyList<EntryId> ids = VaultDragData.Read(e.Data);
            bool copy = effects.HasFlag(DragDropEffects.Copy);
            tree.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = explorer.DropAsync(ids, target, copy));
            return;
        }

        FileDropBehavior.PostImport(tree, explorer, e.Data, target);
    }

    private static DragDropEffects Resolve(
        ExplorerViewModel explorer,
        DragEventArgs e,
        FolderNodeViewModel? node,
        out EntryId target)
    {
        target = node?.Id ?? EntryId.Root;

        if (node is null)
        {
            return DragDropEffects.None;
        }

        if (VaultDragData.IsInternal(e.Data, explorer.VaultPath))
        {
            IReadOnlyList<EntryId> ids = VaultDragData.Read(e.Data);
            if (!explorer.CanDrop(ids, target))
            {
                return DragDropEffects.None;
            }

            return (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        }

        return e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private static FolderNodeViewModel? NodeUnder(TreeView tree, Point position)
    {
        DependencyObject? hit = tree.InputHitTest(position) as DependencyObject;
        return VisualSearch.Ancestor<TreeViewItem>(hit)?.DataContext as FolderNodeViewModel;
    }

    private sealed class DropState : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private FolderNodeViewModel? _node;

        public DropState()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = HoverExpandDelay };
            _timer.Tick += OnTick;
        }

        public void Hover(FolderNodeViewModel? node)
        {
            if (ReferenceEquals(_node, node))
            {
                return;
            }

            if (_node is not null)
            {
                _node.IsDropTarget = false;
            }

            _node = node;
            _timer.Stop();

            if (node is null)
            {
                return;
            }

            node.IsDropTarget = true;

            if (!node.IsExpanded && node.HasSubfolders)
            {
                _timer.Start();
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            if (_node is not null)
            {
                _node.IsDropTarget = false;
                _node = null;
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _timer.Stop();
            if (_node is not null)
            {
                _node.IsExpanded = true;
            }
        }
    }
}
