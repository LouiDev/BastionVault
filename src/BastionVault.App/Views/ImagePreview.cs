using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace BastionVault.App.Views;

/// <summary>
/// Shows an image the preview pane has already read into memory. The decode is capped two ways:
/// <see cref="DecodePixelWidth"/> follows the pane's width so a 40 megapixel photograph is never
/// rasterised at full size, and an image whose declared dimensions exceed
/// <see cref="MaxPixels"/> is refused outright rather than allowed to allocate gigabytes. Nothing
/// here writes to disk: the bytes come from the vault and stay in memory.
/// </summary>
/// <remarks>
/// The bytes come from a vault, so they are attacker-controlled: a decode must never be able to
/// throw out of this class. WIC surfaces a wide and undocumented set of exception types for
/// malformed input (a ten-byte truncated GIF raises <see cref="IOException"/>, third-party codecs
/// raise COM errors), and the same bytes are decoded again on every pane resize because
/// <see cref="DecodePixelWidth"/> is pushed from the pane's SizeChanged handler. So every failure
/// is caught, turned into <see cref="Failure"/>, and remembered against the byte array that caused
/// it: a later width change re-reports the failure instead of re-entering the decoder.
/// </remarks>
public sealed class ImagePreview : Image
{
    /// <summary>Images larger than 64 megapixels are refused.</summary>
    public const long MaxPixels = 64L * 1000 * 1000;

    /// <summary>Identifies the <see cref="Bytes"/> dependency property.</summary>
    public static readonly DependencyProperty BytesProperty = DependencyProperty.Register(
        nameof(Bytes), typeof(byte[]), typeof(ImagePreview), new PropertyMetadata(null, OnSourceInputChanged));

    /// <summary>Identifies the <see cref="DecodePixelWidth"/> dependency property.</summary>
    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.Register(
        nameof(DecodePixelWidth), typeof(int), typeof(ImagePreview), new PropertyMetadata(0, OnSourceInputChanged));

    private static readonly DependencyPropertyKey FailurePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Failure), typeof(string), typeof(ImagePreview), new PropertyMetadata(null));

    /// <summary>Identifies the read-only <see cref="Failure"/> dependency property.</summary>
    public static readonly DependencyProperty FailureProperty = FailurePropertyKey.DependencyProperty;

    /// <summary>The byte array a decode has already failed on, so it is never decoded again.</summary>
    private byte[]? _poisoned;

    /// <summary>The message <see cref="_poisoned"/> failed with.</summary>
    private string? _poisonedFailure;

    /// <summary>The encoded image bytes, or <see langword="null"/> for nothing to show.</summary>
    public byte[]? Bytes
    {
        get => (byte[]?)GetValue(BytesProperty);
        set => SetValue(BytesProperty, value);
    }

    /// <summary>Width to decode at, in pixels; zero or less decodes at the natural size.</summary>
    public int DecodePixelWidth
    {
        get => (int)GetValue(DecodePixelWidthProperty);
        set => SetValue(DecodePixelWidthProperty, value);
    }

    /// <summary>Why the image is not shown, or <see langword="null"/> when it is.</summary>
    public string? Failure => (string?)GetValue(FailureProperty);

    private static void OnSourceInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ImagePreview)d).Rebuild();

    private void Rebuild()
    {
        Source = null;
        SetValue(FailurePropertyKey, null);

        byte[]? bytes = Bytes;
        if (bytes is null || bytes.Length == 0)
        {
            _poisoned = null;
            _poisonedFailure = null;
            return;
        }

        if (ReferenceEquals(bytes, _poisoned))
        {
            // Already known bad. A resize must not hand the same malformed bytes back to WIC.
            SetValue(FailurePropertyKey, _poisonedFailure);
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);

            BitmapDecoder probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (probe.Frames.Count == 0)
            {
                Fail(bytes, "This image has no frames.");
                return;
            }

            BitmapFrame frame = probe.Frames[0];
            long pixels = (long)frame.PixelWidth * frame.PixelHeight;
            if (pixels > MaxPixels)
            {
                Fail(bytes, $"This image is {frame.PixelWidth} by {frame.PixelHeight} pixels, too large to preview.");
                return;
            }

            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;

            int width = DecodePixelWidth;
            if (width > 0 && width < frame.PixelWidth)
            {
                image.DecodePixelWidth = width;
            }

            image.EndInit();
            image.Freeze();

            Source = image;
            _poisoned = null;
            _poisonedFailure = null;
        }
        catch (Exception)
        {
            // Deliberately unfiltered. WIC raises NotSupportedException, ArgumentException,
            // FileFormatException, OverflowException, OutOfMemoryException, IOException and
            // COMException for different flavours of malformed input, and a decode fault here
            // would otherwise escape a resize straight into the dispatcher's crash handler.
            Fail(bytes, "This image could not be decoded.");
        }
    }

    private void Fail(byte[] bytes, string message)
    {
        Source = null;
        _poisoned = bytes;
        _poisonedFailure = message;
        SetValue(FailurePropertyKey, message);
    }
}
