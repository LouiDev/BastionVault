using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bastion.App.Behaviors;

/// <summary>
/// The label that follows the cursor during an internal drag: what is being moved, and - when the
/// pointer leaves anywhere that will accept it - why it will not be dropped there. It is an
/// adorner rather than a drag image so it can change its text mid-drag.
/// </summary>
public sealed partial class DragAdorner : Adorner
{
    private static readonly Typeface Face = new("Segoe UI Variable Text, Segoe UI");

    private readonly TranslateTransform _offset = new();
    private FormattedText? _text;
    private bool _isRefusal;

    /// <summary>Creates the adorner over the element whose layer will host it.</summary>
    /// <param name="adornedElement">Usually the explorer root.</param>
    public DragAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        AllowDrop = false;
    }

    /// <summary>Puts an adorner on an element's layer, or returns <see langword="null"/> when it has none.</summary>
    /// <param name="element">Element whose adorner layer should host the label.</param>
    public static DragAdorner? Attach(UIElement? element)
    {
        if (element is null)
        {
            return null;
        }

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(element);
        if (layer is null)
        {
            return null;
        }

        var adorner = new DragAdorner(element);
        layer.Add(adorner);
        return adorner;
    }

    /// <summary>Removes the adorner from its layer.</summary>
    public void Detach()
    {
        AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);
    }

    /// <summary>Moves the label to the current cursor position and sets its text.</summary>
    /// <param name="text">What to say.</param>
    /// <param name="isRefusal">True to draw the label in the danger colour.</param>
    public void Update(string text, bool isRefusal)
    {
        if (!TryGetCursorPosition(out Point screen))
        {
            return;
        }

        Point local;
        try
        {
            local = AdornedElement.PointFromScreen(screen);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _offset.X = local.X + 18;
        _offset.Y = local.Y + 14;
        _isRefusal = isRefusal;
        _text = Format(text);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var group = new GeneralTransformGroup();
        if (transform is not null)
        {
            group.Children.Add(transform);
        }

        group.Children.Add(_offset);
        return group;
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        if (_text is null)
        {
            return;
        }

        var box = new Rect(0, 0, _text.Width + 20, _text.Height + 10);
        Brush background = Resource("Brush.Bg2") ?? Brushes.Black;
        Brush stroke = Resource(_isRefusal ? "Brush.DangerText" : "Brush.AccentDim") ?? Brushes.Gray;

        var pen = new Pen(stroke, 1);
        pen.Freeze();

        drawingContext.DrawRoundedRectangle(background, pen, box, 4, 4);
        drawingContext.DrawText(_text, new Point(10, 5));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out PointI point);

    private static bool TryGetCursorPosition(out Point point)
    {
        if (GetCursorPos(out PointI native))
        {
            point = new Point(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    private Brush? Resource(string key) => TryFindResource(key) as Brush;

    private FormattedText Format(string text) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        Face,
        13,
        Resource(_isRefusal ? "Brush.DangerText" : "Brush.TextPrimary") ?? Brushes.White,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI
    {
        public int X;
        public int Y;
    }
}
