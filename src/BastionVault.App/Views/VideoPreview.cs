using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BastionVault.App.Services;

namespace BastionVault.App.Views;

/// <summary>
/// Shows the still frame the preview pane got from the video thumbnailer. The frame arrives as
/// plain BGRA rows already reduced to the pane's width, so there is no decoder here and nothing to
/// cap: the bitmap is built straight from the bytes and frozen. Nothing touches disk.
/// </summary>
/// <remarks>
/// <see cref="BitmapSource.Create(int, int, double, double, PixelFormat, BitmapPalette, Array, int)"/>
/// copies the pixels into an unmanaged WIC buffer that cannot be zeroed, the same residual the
/// image preview has (THREAT-MODEL.md A6). The managed array is the view model's and is zeroed
/// there when the selection moves on.
/// </remarks>
public sealed class VideoPreview : Image
{
    /// <summary>Identifies the <see cref="Frame"/> dependency property.</summary>
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(VideoFrame), typeof(VideoPreview), new PropertyMetadata(null, OnFrameChanged));

    /// <summary>The frame to show, or <see langword="null"/> for nothing.</summary>
    public VideoFrame? Frame
    {
        get => (VideoFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    private static void OnFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (VideoPreview)d;
        view.Source = ToBitmap(e.NewValue as VideoFrame);
    }

    /// <summary>Builds a frozen bitmap from a frame, or <see langword="null"/> for a missing or inconsistent one.</summary>
    /// <param name="frame">The frame; its pixel buffer must hold <c>Width * Height * 4</c> bytes.</param>
    public static BitmapSource? ToBitmap(VideoFrame? frame)
    {
        if (frame is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }

        int stride = frame.Width * 4;
        if ((long)stride * frame.Height > frame.Pixels.Length)
        {
            return null;
        }

        BitmapSource bitmap = BitmapSource.Create(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
