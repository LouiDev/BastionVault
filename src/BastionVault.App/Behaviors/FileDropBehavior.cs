using System.Windows;
using System.Windows.Threading;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Accepts files dropped from Explorer onto the explorer and imports them into the folder being
/// shown. The drop handler does no work inside the OLE loop: it copies the path array out of the
/// event, marks the event handled, and posts the import at background priority
/// (UI-CONTRACT.md section 1.7).
/// </summary>
public static class FileDropBehavior
{
    /// <summary>Identifies the <c>FileDropBehavior.Explorer</c> attached property.</summary>
    public static readonly DependencyProperty ExplorerProperty = DependencyProperty.RegisterAttached(
        "Explorer", typeof(ExplorerViewModel), typeof(FileDropBehavior),
        new PropertyMetadata(null, OnExplorerChanged));

    private static readonly DependencyPropertyKey IsDropActivePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsDropActive", typeof(bool), typeof(FileDropBehavior), new PropertyMetadata(false));

    /// <summary>Identifies the read-only <c>FileDropBehavior.IsDropActive</c> attached property.</summary>
    public static readonly DependencyProperty IsDropActiveProperty = IsDropActivePropertyKey.DependencyProperty;

    /// <summary>Reads the explorer that dropped files are imported into.</summary>
    /// <param name="element">The drop target.</param>
    public static ExplorerViewModel? GetExplorer(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (ExplorerViewModel?)element.GetValue(ExplorerProperty);
    }

    /// <summary>Makes an element accept Explorer file drops for an explorer.</summary>
    /// <param name="element">The drop target.</param>
    /// <param name="value">The explorer, or <see langword="null"/> to detach.</param>
    public static void SetExplorer(DependencyObject element, ExplorerViewModel? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ExplorerProperty, value);
    }

    /// <summary>True while a file drag is over the element; the view draws a border.</summary>
    /// <param name="element">The drop target.</param>
    public static bool GetIsDropActive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsDropActiveProperty);
    }

    /// <summary>
    /// Copies the paths out of the drop data and posts the import; used by the list and the tree
    /// too, so every file drop in the explorer takes the same route out of the OLE loop.
    /// </summary>
    /// <param name="element">The element that handled the drop, used for its dispatcher.</param>
    /// <param name="explorer">The explorer to import into.</param>
    /// <param name="data">The dropped data.</param>
    /// <param name="parent">Folder to import into, or <see langword="null"/> for the current one.</param>
    /// <returns>True when an import was posted.</returns>
    public static bool PostImport(DependencyObject element, ExplorerViewModel? explorer, IDataObject? data, Core.EntryId? parent)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (explorer is null)
        {
            return false;
        }

        IReadOnlyList<string> paths = VaultDragData.ReadFileDrop(data);
        if (paths.Count == 0)
        {
            return false;
        }

        Dispatcher dispatcher = element.Dispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = explorer.ImportPathsAsync(paths, parent));
        return true;
    }

    private static void OnExplorerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.DragEnter -= OnDragOver;
        element.DragOver -= OnDragOver;
        element.DragLeave -= OnDragLeave;
        element.Drop -= OnDrop;

        if (e.NewValue is not ExplorerViewModel)
        {
            element.SetValue(IsDropActivePropertyKey, false);
            return;
        }

        element.AllowDrop = true;
        element.DragEnter += OnDragOver;
        element.DragOver += OnDragOver;
        element.DragLeave += OnDragLeave;
        element.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        bool accepts = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        element.SetValue(IsDropActivePropertyKey, accepts);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.SetValue(IsDropActivePropertyKey, false);
        }
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        element.SetValue(IsDropActivePropertyKey, false);

        if (PostImport(element, GetExplorer(element), e.Data, null))
        {
            e.Handled = true;
        }
    }
}
