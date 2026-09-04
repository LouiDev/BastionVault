using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BastionVault.App.Controls;

/// <summary>
/// A <see cref="Border"/> whose top-left and bottom-right corners are cut at 45 degrees.
/// Nothing else in Bastion Vault is chamfered: the shape is reserved for the vault chip in the
/// title bar and on the unlock card (UI-CONTRACT.md section 2, signature detail 5).
/// </summary>
public sealed class ChamferedBorder : Border
{
    /// <summary>Identifies the <see cref="ChamferSize"/> dependency property.</summary>
    public static readonly DependencyProperty ChamferSizeProperty = DependencyProperty.Register(
        nameof(ChamferSize), typeof(double), typeof(ChamferedBorder),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Length of the 45 degree cut, in device-independent pixels. Defaults to 8.</summary>
    public double ChamferSize
    {
        get => (double)GetValue(ChamferSizeProperty);
        set => SetValue(ChamferSizeProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = BorderThickness.Left;
        double inset = thickness / 2;
        double cut = Math.Max(0, Math.Min(ChamferSize, Math.Min(width, height) / 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(inset + cut, inset), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - inset, inset), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(width - inset, height - inset - cut), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(width - inset - cut, height - inset), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(inset, height - inset), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(inset, inset + cut), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();

        Pen? pen = thickness > 0 && BorderBrush is not null ? new Pen(BorderBrush, thickness) : null;
        pen?.Freeze();
        drawingContext.DrawGeometry(Background, pen, geometry);
    }
}
