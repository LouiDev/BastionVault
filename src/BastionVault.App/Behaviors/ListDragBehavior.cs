using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BastionVault.App.ViewModels;
using BastionVault.Core;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Makes the entry list a drag source and a drop target for entries inside the vault. The drag
/// carries ids only, a drop onto a folder row moves (or copies with Ctrl) into that folder, and a
/// drag that leaves the app is refused with the adorner saying what to use instead
/// (UI-CONTRACT.md section 1.7).
/// </summary>
public static class ListDragBehavior
{
    /// <summary>Identifies the <c>ListDragBehavior.Explorer</c> attached property.</summary>
    public static readonly DependencyProperty ExplorerProperty = DependencyProperty.RegisterAttached(
        "Explorer", typeof(ExplorerViewModel), typeof(ListDragBehavior),
        new PropertyMetadata(null, OnExplorerChanged));

    private static readonly DependencyPropertyKey IsDropTargetPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsDropTarget", typeof(bool), typeof(ListDragBehavior), new PropertyMetadata(false));

    /// <summary>Identifies the read-only <c>ListDragBehavior.IsDropTarget</c> attached property.</summary>
    public static readonly DependencyProperty IsDropTargetProperty = IsDropTargetPropertyKey.DependencyProperty;

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DragState), typeof(ListDragBehavior), new PropertyMetadata(null));

    /// <summary>Reads the explorer the list drags for.</summary>
    /// <param name="element">The list.</param>
    public static ExplorerViewModel? GetExplorer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ExplorerViewModel?)element.GetValue(ExplorerProperty);
    }

    /// <summary>Wires a list up as a drag source and drop target.</summary>
    /// <param name="element">The list.</param>
    /// <param name="value">The explorer, or <see langword="null"/> to detach.</param>
    public static void SetExplorer(DependencyObject element, ExplorerViewModel? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplorerProperty, value);
    }

    /// <summary>True while a drag hovers over this row and it would accept the drop.</summary>
    /// <param name="element">A list row.</param>
    public static bool GetIsDropTarget(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsDropTargetProperty);
    }

    private static void OnExplorerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
        {
            return;
        }

        list.PreviewMouseLeftButtonDown -= OnMouseDown;
        list.PreviewMouseMove -= OnMouseMove;
        list.DragOver -= OnDragOver;
        list.DragLeave -= OnDragLeave;
        list.Drop -= OnDrop;
        list.QueryContinueDrag -= OnQueryContinueDrag;
        list.GiveFeedback -= OnGiveFeedback;

        if (e.NewValue is not ExplorerViewModel)
        {
            list.SetValue(StateProperty, null);
            return;
        }

        list.SetValue(StateProperty, new DragState());
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.DragOver += OnDragOver;
        list.DragLeave += OnDragLeave;
        list.Drop += OnDrop;
        list.QueryContinueDrag += OnQueryContinueDrag;
        list.GiveFeedback += OnGiveFeedback;
    }

    private static DragState? StateOf(DependencyObject element) => (DragState?)element.GetValue(StateProperty);

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list || StateOf(list) is not { } state)
        {
            return;
        }

        // Never start a drag out of the inline rename editor.
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            state.Candidate = null;
            return;
        }

        state.Origin = e.GetPosition(list);
        state.Candidate = ItemUnder(e.OriginalSource as DependencyObject);
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListView list
            || StateOf(list) is not { Candidate: { } candidate } state
            || state.IsDragging
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(list) - state.Origin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        StartDrag(list, state, candidate);
    }

    private static void StartDrag(ListView list, DragState state, EntryItemViewModel candidate)
    {
        if (GetExplorer(list) is not { } explorer)
        {
            return;
        }

        List<EntryItemViewModel> dragged = [.. list.SelectedItems.OfType<EntryItemViewModel>()];
        if (!dragged.Contains(candidate))
        {
            dragged = [candidate];
        }

        state.IsDragging = true;
        state.Count = dragged.Count;
        state.Adorner = DragAdorner.Attach(VisualSearch.Ancestor<UserControl>(list) ?? (UIElement)list);

        try
        {
            DataObject data = VaultDragData.Create([.. dragged.Select(i => i.Id)], explorer.VaultPath);
            DragDrop.DoDragDrop(list, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            state.Adorner?.Detach();
            state.Adorner = null;
            state.IsDragging = false;
            state.Candidate = null;
            ClearHighlight(list);
        }
    }

    private static void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (sender is not ListView list || StateOf(list) is not { Adorner: { } adorner } state)
        {
            return;
        }

        e.UseDefaultCursors = true;
        e.Handled = true;

        bool refused = e.Effects == DragDropEffects.None;
        string text = refused
            ? VaultDragData.RefusalText
            : e.Effects.HasFlag(DragDropEffects.Copy)
                ? $"Copy {state.Count} item{(state.Count == 1 ? string.Empty : "s")}"
                : $"Move {state.Count} item{(state.Count == 1 ? string.Empty : "s")}";

        adorner.Update(text, refused);
    }

    private static void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            e.Action = DragAction.Cancel;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView list || GetExplorer(list) is not { } explorer)
        {
            return;
        }

        EdgeAutoScroll.Update(list, e.GetPosition(list));

        e.Effects = Resolve(list, explorer, e, out _, out ListViewItem? row);
        Highlight(list, e.Effects == DragDropEffects.None ? null : row);
        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListView list)
        {
            ClearHighlight(list);
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListView list || GetExplorer(list) is not { } explorer)
        {
            return;
        }

        DragDropEffects effects = Resolve(list, explorer, e, out EntryId target, out _);
        ClearHighlight(list);
        e.Handled = true;

        if (effects == DragDropEffects.None)
        {
            return;
        }

        if (VaultDragData.IsInternal(e.Data, explorer.VaultPath))
        {
            IReadOnlyList<EntryId> ids = VaultDragData.Read(e.Data);
            bool copy = effects.HasFlag(DragDropEffects.Copy);
            list.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = explorer.DropAsync(ids, target, copy));
            return;
        }

        FileDropBehavior.PostImport(list, explorer, e.Data, target);
    }

    private static DragDropEffects Resolve(
        ListView list,
        ExplorerViewModel explorer,
        DragEventArgs e,
        out EntryId target,
        out ListViewItem? row)
    {
        target = explorer.CurrentFolder;
        row = null;

        Point position = e.GetPosition(list);
        DependencyObject? hit = list.InputHitTest(position) as DependencyObject;
        ListViewItem? container = VisualSearch.Ancestor<ListViewItem>(hit);

        if (container?.DataContext is EntryItemViewModel { IsFolder: true } folder)
        {
            target = folder.Id;
            row = container;
        }

        if (VaultDragData.IsInternal(e.Data, explorer.VaultPath))
        {
            IReadOnlyList<EntryId> ids = VaultDragData.Read(e.Data);
            if (!explorer.CanDrop(ids, target))
            {
                row = null;
                return DragDropEffects.None;
            }

            return (e.KeyStates & DragDropKeyStates.ControlKey) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            return DragDropEffects.Copy;
        }

        row = null;
        return DragDropEffects.None;
    }

    private static EntryItemViewModel? ItemUnder(DependencyObject? source) =>
        VisualSearch.Ancestor<ListViewItem>(source)?.DataContext as EntryItemViewModel;

    private static void Highlight(ListView list, ListViewItem? row)
    {
        if (StateOf(list) is not { } state)
        {
            return;
        }

        if (ReferenceEquals(state.Highlighted, row))
        {
            return;
        }

        state.Highlighted?.SetValue(IsDropTargetPropertyKey, false);
        state.Highlighted = row;
        row?.SetValue(IsDropTargetPropertyKey, true);
    }

    private static void ClearHighlight(ListView list) => Highlight(list, null);

    private sealed class DragState
    {
        public Point Origin { get; set; }

        public EntryItemViewModel? Candidate { get; set; }

        public bool IsDragging { get; set; }

        public int Count { get; set; }

        public DragAdorner? Adorner { get; set; }

        public ListViewItem? Highlighted { get; set; }
    }
}
