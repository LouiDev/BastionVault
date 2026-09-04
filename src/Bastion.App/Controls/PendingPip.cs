using System.Windows;
using System.Windows.Media;

namespace Bastion.App.Controls;

/// <summary>
/// What a status-rail pip reports about an entry (UI-CONTRACT.md section 2, signature detail 2).
/// </summary>
public enum PipState
{
    /// <summary>Nothing to report; the pip is not drawn.</summary>
    None,

    /// <summary>New since the last save: a filled amber dot.</summary>
    Added,

    /// <summary>Renamed, moved or edited since the last save: an amber ring.</summary>
    Changed,

    /// <summary>Failed the last integrity check: a filled red dot.</summary>
    Failed,
}

/// <summary>
/// The 8 px pip drawn in the 12 px status rail at the left edge of a list row, and the 4 px
/// dot a folder shows in the tree when any descendant is pending. Drawn directly so a list of
/// thousands of rows costs one visual per row instead of a template.
/// </summary>
public sealed class PendingPip : FrameworkElement
{
    /// <summary>Identifies the <see cref="State"/> dependency property.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(PipState), typeof(PendingPip),
        new FrameworkPropertyMetadata(PipState.None, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Diameter"/> dependency property.</summary>
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(PendingPip),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What the pip reports.</summary>
    public PipState State
    {
        get => (PipState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Diameter of the dot. 8 in a list row, 4 in the folder tree.</summary>
    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Diameter, Diameter);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        PipState state = State;
        if (state == PipState.None)
        {
            return;
        }

        double radius = Diameter / 2;
        var centre = new Point(RenderSize.Width / 2, RenderSize.Height / 2);

        switch (state)
        {
            case PipState.Added:
                drawingContext.DrawEllipse(Brush("Brush.Accent"), null, centre, radius, radius);
                break;

            case PipState.Changed:
                var pen = new Pen(Brush("Brush.Accent"), 1.5);
                pen.Freeze();
                drawingContext.DrawEllipse(null, pen, centre, radius - 0.75, radius - 0.75);
                break;

            case PipState.Failed:
                drawingContext.DrawEllipse(Brush("Brush.DangerText"), null, centre, radius, radius);
                break;

            default:
                break;
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;
}
