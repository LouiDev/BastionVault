using System.Windows;
using System.Windows.Media;

namespace BastionVault.App.Views;

/// <summary>
/// Draws the peaks the audio prober returned as a column of bars mirrored around a centre line, one bar
/// per peak, stretched across whatever width the pane gives it. It is content, not a control: the bars
/// take a greyscale brush (UI-CONTRACT.md section 1.9) and nothing here reacts to the pointer.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    /// <summary>Identifies the <see cref="Peaks"/> dependency property.</summary>
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(float[]), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Fill"/> dependency property.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="Baseline"/> dependency property.</summary>
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(
        nameof(Baseline), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Peak amplitude per column in [0, 1], left to right; <see langword="null"/> draws nothing.</summary>
    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>Brush of the bars.</summary>
    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Brush of the centre line, drawn across the whole width whether or not there are peaks.</summary>
    public Brush Baseline
    {
        get => (Brush)GetValue(BaselineProperty);
        set => SetValue(BaselineProperty, value);
    }

    /// <summary>The gap between bars, in device-independent pixels, once a bar is wide enough to afford one.</summary>
    private const double Gap = 1;

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

        double middle = Math.Round(height / 2);
        drawingContext.DrawRectangle(Baseline, null, new Rect(0, middle, width, 1));

        float[]? peaks = Peaks;
        if (peaks is null || peaks.Length == 0)
        {
            return;
        }

        double slot = width / peaks.Length;
        double bar = slot >= 3 ? slot - Gap : slot;
        double half = Math.Max(1, (height / 2) - 1);
        Brush fill = Fill;

        for (int i = 0; i < peaks.Length; i++)
        {
            double amplitude = Math.Clamp(peaks[i], 0f, 1f);
            double extent = Math.Max(1, Math.Round(amplitude * half));
            double x = Math.Round(i * slot);
            drawingContext.DrawRectangle(fill, null, new Rect(x, middle - extent, Math.Max(1, Math.Round(bar)), (extent * 2) + 1));
        }
    }
}
