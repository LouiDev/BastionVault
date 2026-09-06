using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Views;

/// <summary>
/// The preview pane. Text, an image or a hex dump of whatever is selected, read into memory only.
/// The pane tells the view model how wide it is so an image is decoded at the size it will be
/// drawn at rather than at its full resolution, and so the hex dump puts 16 or 8 bytes on a line.
/// </summary>
public partial class PreviewPaneView : UserControl
{
    /// <summary>Creates the pane.</summary>
    public PreviewPaneView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => PublishWidth();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            PublishWidth();
        }
    }

    /// <summary>Horizontal padding of the hex scroll viewer plus room for its vertical scrollbar.</summary>
    private const double HexChrome = 24 + 18;

    private void PublishWidth()
    {
        if (DataContext is not PreviewViewModel preview)
        {
            return;
        }

        PublishHexColumns(preview);

        // Decode at the pane's pixel width, rounded up to the next 64 so a slow drag of the
        // splitter does not re-decode the image on every frame.
        double dpi = VisualTreeHelperDpi();
        int pixels = (int)Math.Ceiling(Math.Max(64, ActualWidth - 24) * dpi / 64) * 64;

        try
        {
            // This assignment re-enters the image decoder through the DecodePixelWidth binding.
            // ImagePreview already turns every decode fault into its Failure string, but a
            // resize must never be able to reach the dispatcher's crash handler, which zeroes
            // the vault keys and shuts the app down.
            preview.DecodeWidth = pixels;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Preview decode width could not be applied: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Measures how many monospace characters fit across the pane and lets the view model pick 16 or 8
    /// bytes per line from that. Measured, not assumed: the mono face and its size come from the theme.
    /// </summary>
    /// <param name="preview">The pane's view model.</param>
    private void PublishHexColumns(PreviewViewModel preview)
    {
        double available = ActualWidth - HexChrome;
        if (available <= 0 || HexText.FontFamily is null)
        {
            return;
        }

        try
        {
            var probe = new FormattedText(
                "0",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(HexText.FontFamily, HexText.FontStyle, HexText.FontWeight, HexText.FontStretch),
                HexText.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double column = probe.WidthIncludingTrailingWhitespace;
            if (column <= 0)
            {
                return;
            }

            preview.HexBytesPerLine = PreviewViewModel.HexBytesPerLineFor((int)Math.Floor(available / column));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A font that cannot be measured yet (before the theme applied) keeps the previous figure.
            System.Diagnostics.Debug.WriteLine($"Hex columns could not be measured: {ex.GetType().Name}");
        }
    }

    private double VisualTreeHelperDpi()
    {
        try
        {
            return System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            return 1.0;
        }
    }
}
