using System.Windows;
using System.Windows.Controls;
using Bastion.App.ViewModels;

namespace Bastion.App.Views;

/// <summary>
/// The preview pane. Text, an image or a hex dump of whatever is selected, read into memory only.
/// The pane tells the view model how wide it is so an image is decoded at the size it will be
/// drawn at rather than at its full resolution.
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

    private void PublishWidth()
    {
        if (DataContext is not PreviewViewModel preview)
        {
            return;
        }

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
