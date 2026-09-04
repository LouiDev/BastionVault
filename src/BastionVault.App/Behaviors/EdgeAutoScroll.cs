using System.Windows;
using System.Windows.Controls;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Scrolls a list or tree while a drag hovers near its top or bottom edge, so a drop into a folder
/// that is off screen does not need a separate scroll gesture.
/// </summary>
public static class EdgeAutoScroll
{
    /// <summary>How close to an edge the pointer has to be, in device-independent pixels.</summary>
    private const double EdgeBand = 28;

    /// <summary>How far one hover step scrolls.</summary>
    private const double Step = 24;

    /// <summary>Scrolls when the pointer is inside the edge band.</summary>
    /// <param name="control">The list or tree being dragged over.</param>
    /// <param name="pointerInControl">Pointer position in the control's coordinates.</param>
    public static void Update(ItemsControl? control, Point pointerInControl)
    {
        if (control is null)
        {
            return;
        }

        ScrollViewer? viewer = VisualSearch.Descendant<ScrollViewer>(control);
        if (viewer is null || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        if (pointerInControl.Y < EdgeBand)
        {
            viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - Step));
        }
        else if (pointerInControl.Y > control.ActualHeight - EdgeBand)
        {
            viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + Step));
        }
    }
}
