using System.Windows;

namespace BastionVault.App.Controls;

/// <summary>
/// Attached properties the item containers of the tree and the list read. They are the seam
/// between the theme (which owns the status rail and the selection rail) and the explorer view
/// models (which know whether an entry is pending). Bind them in an
/// <c>ItemContainerStyle</c>; nothing in the theme knows about explorer types.
/// Both properties inherit, so a cell template inside a <c>GridViewRowPresenter</c> can read them
/// with <c>RelativeSource Self</c>; a <c>FindAncestor</c> walk to the <c>ListViewItem</c> does not
/// resolve from there, which is why inheritance rather than an ancestor lookup carries the value.
/// </summary>
public static class ItemAdornments
{
    /// <summary>Identifies the <c>ItemAdornments.Pip</c> attached property.</summary>
    public static readonly DependencyProperty PipProperty = DependencyProperty.RegisterAttached(
        "Pip", typeof(PipState), typeof(ItemAdornments),
        new FrameworkPropertyMetadata(
            PipState.None,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Identifies the <c>ItemAdornments.IsCut</c> attached property.</summary>
    public static readonly DependencyProperty IsCutProperty = DependencyProperty.RegisterAttached(
        "IsCut", typeof(bool), typeof(ItemAdornments),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>Reads the pending pip shown in the status rail of <paramref name="element"/>.</summary>
    /// <param name="element">The item container.</param>
    public static PipState GetPip(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (PipState)element.GetValue(PipProperty);
    }

    /// <summary>Sets the pending pip shown in the status rail of <paramref name="element"/>.</summary>
    /// <param name="element">The item container.</param>
    /// <param name="value">The pip to show.</param>
    public static void SetPip(DependencyObject element, PipState value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PipProperty, value);
    }

    /// <summary>True when the item is on the internal clipboard as a cut; the row renders faded.</summary>
    /// <param name="element">The item container.</param>
    public static bool GetIsCut(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsCutProperty);
    }

    /// <summary>Marks the item as cut so the row renders faded.</summary>
    /// <param name="element">The item container.</param>
    /// <param name="value">True when the item is on the internal clipboard as a cut.</param>
    public static void SetIsCut(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsCutProperty, value);
    }
}
