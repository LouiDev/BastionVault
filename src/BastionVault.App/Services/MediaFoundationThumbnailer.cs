using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using static BastionVault.App.Services.MediaFoundationInterop;

namespace BastionVault.App.Services;

/// <summary>
/// <see cref="IVideoThumbnailer"/> over Media Foundation's source reader. The vault stream is wrapped as
/// a COM <c>IStream</c> and handed to the media stack directly, so the plaintext never leaves process
/// memory: no temp file, no URL, no file handle. One frame from early in the video is converted to
/// 32-bit RGB by the reader's own video processor, copied out, reduced to the requested width and
/// returned; every intermediate buffer is zeroed on the way out.
/// </summary>
/// <remarks>
/// <para>
/// The bytes come from a vault, so they are attacker-controlled, and a video demuxer plus decoder is
/// a far larger parser than an image codec. Three decisions follow: hardware (DXVA) decoding is
/// switched off, so frames stay in system memory and no driver code runs on the input; every
/// <c>HRESULT</c> is a decision rather than an exception; and no failure of any kind escapes this
/// class except cancellation. Windows N editions without the Media Feature Pack have no Media
/// Foundation at all, which surfaces as <see cref="DllNotFoundException"/> on the first call and is
/// reported once.
/// </para>
/// <para>
/// The reader is synchronous and runs on a thread-pool thread. Media Foundation pulls bytes through
/// the <c>IStream</c> adapter from its own work-queue threads, so the adapter serialises access to the
/// vault stream and checks the cancellation token on every read, which is what stops a probe when the
/// selection moves on.
/// </para>
/// </remarks>
public sealed class MediaFoundationThumbnailer : IVideoThumbnailer
{
    /// <summary>A frame whose declared area exceeds this is not decoded; the figures are still reported.</summary>
    public const long MaxPixels = 64L * 1000 * 1000;

    /// <summary>The frame is taken this far into the video, capped at <see cref="MaxSeek"/>.</summary>
    private const double SeekFraction = 0.1;

    private static readonly TimeSpan MaxSeek = TimeSpan.FromSeconds(10);

    /// <summary>Samples the reader may deliver (ticks, gaps, frames before the seek point) before the probe gives up.</summary>
    private const int MaxSampleAttempts = 128;

    private const int Ok = 0;

    private readonly ILog _log;
    private int _unavailableReported;

    /// <summary>Creates the thumbnailer.</summary>
    /// <param name="log">Log; only failure kinds are recorded, never content.</param>
    public MediaFoundationThumbnailer(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>True when the Media Foundation libraries can be loaded on this machine.</summary>
    public static bool IsAvailable =>
        NativeLibrary.TryLoad("mfplat.dll", out _) && NativeLibrary.TryLoad("mfreadwrite.dll", out _);

    /// <inheritdoc />
    public Task<VideoProbe?> ProbeAsync(Stream video, string? contentType, int maxWidth, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        if (!video.CanSeek)
        {
            throw new ArgumentException("A video probe needs a seekable stream.", nameof(video));
        }

        return Task.Run(() => Probe(video, contentType, maxWidth, ct), ct);
    }

    private VideoProbe? Probe(Stream video, string? contentType, int maxWidth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return ProbeCore(video, contentType, maxWidth, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (Interlocked.Exchange(ref _unavailableReported, 1) == 0)
            {
                _log.Warn("Media Foundation is not available on this machine; video previews are off.", ex);
            }

            return null;
        }
        catch (Exception ex)
        {
            // The media stack raised through the interop layer or the adapter did: a cancelled read
            // comes back as a COM failure, so ask the token before calling it a bad file.
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            _log.Warn("A video could not be probed.", ex);
            return null;
        }
    }

    private VideoProbe? ProbeCore(Stream video, string? contentType, int maxWidth, CancellationToken ct)
    {
        if (MFStartup(ApiVersion, 0) != Ok)
        {
            return null;
        }

        IMFByteStream? byteStream = null;
        IMFAttributes? readerAttributes = null;
        IMFSourceReader? reader = null;

        try
        {
            var adapter = new StreamAdapter(video, ct);
            if (MFCreateMFByteStreamOnStream(adapter, out byteStream) != Ok)
            {
                return null;
            }

            HintContentType(byteStream, contentType);

            if (MFCreateAttributes(out readerAttributes, 2) != Ok)
            {
                return null;
            }

            Guid key = EnableVideoProcessing;
            readerAttributes.SetUINT32(ref key, 1);
            key = DisableDxva;
            readerAttributes.SetUINT32(ref key, 1);

            // Fails for a container no installed byte-stream handler recognises: not a video, as far as
            // this machine is concerned.
            if (MFCreateSourceReaderFromByteStream(byteStream, readerAttributes, out reader) != Ok)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }

            reader.SetStreamSelection(AllStreams, 0);
            if (reader.SetStreamSelection(FirstVideoStream, 1) != Ok)
            {
                // A recognised container without a video track (audio in an MP4, say).
                return null;
            }

            (int width, int height) = NativeFrameSize(reader);
            TimeSpan? duration = Duration(reader);

            VideoFrame? frame = null;
            if (width > 0 && height > 0 && (long)width * height <= MaxPixels)
            {
                frame = DecodeFrame(reader, duration, width, height, maxWidth, ct);
            }

            return new VideoProbe(width, height, duration, frame);
        }
        finally
        {
            Release(reader);
            Release(readerAttributes);
            Release(byteStream);
            MFShutdown();
        }
    }

    /// <summary>Tells the source resolver what the file name claimed; a lie only costs a failed probe.</summary>
    private static void HintContentType(IMFByteStream byteStream, string? contentType)
    {
        if (string.IsNullOrEmpty(contentType) || byteStream is not IMFAttributes attributes)
        {
            return;
        }

        Guid key = ByteStreamContentType;
        attributes.SetString(ref key, contentType);
    }

    private static (int Width, int Height) NativeFrameSize(IMFSourceReader reader)
    {
        if (reader.GetNativeMediaType(FirstVideoStream, 0, out IMFMediaType native) != Ok)
        {
            return (0, 0);
        }

        try
        {
            return FrameSizeOf(native);
        }
        finally
        {
            Release(native);
        }
    }

    private static (int Width, int Height) FrameSizeOf(IMFMediaType type)
    {
        Guid key = FrameSize;
        if (type.GetUINT64(ref key, out ulong packed) != Ok)
        {
            return (0, 0);
        }

        uint width = (uint)(packed >> 32);
        uint height = (uint)(packed & 0xFFFF_FFFF);
        return width > int.MaxValue || height > int.MaxValue ? (0, 0) : ((int)width, (int)height);
    }

    private static TimeSpan? Duration(IMFSourceReader reader)
    {
        Guid key = PresentationDuration;
        if (reader.GetPresentationAttribute(MediaSource, ref key, out PropVariant value) != Ok)
        {
            return null;
        }

        try
        {
            if (value.Type != VtUi8 || value.UInt64 > (ulong)TimeSpan.MaxValue.Ticks)
            {
                return null;
            }

            // Media Foundation counts 100-nanosecond units, the same as TimeSpan ticks.
            return TimeSpan.FromTicks((long)value.UInt64);
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static VideoFrame? DecodeFrame(IMFSourceReader reader, TimeSpan? duration, int nativeWidth, int nativeHeight, int maxWidth, CancellationToken ct)
    {
        if (MFCreateMediaType(out IMFMediaType wanted) != Ok)
        {
            return null;
        }

        try
        {
            Guid key = MajorType;
            Guid value = MediaTypeVideo;
            wanted.SetGUID(ref key, ref value);
            key = Subtype;
            value = VideoFormatRgb32;
            wanted.SetGUID(ref key, ref value);

            // Fails when no decoder for the codec is installed (HEVC without the extension, say). The
            // container was still read, so the caller gets the figures and no picture.
            if (reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, wanted) != Ok)
            {
                return null;
            }
        }
        finally
        {
            Release(wanted);
        }

        SeekEarly(reader, duration);

        for (int attempt = 0; attempt < MaxSampleAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            int hr = reader.ReadSample(FirstVideoStream, 0, out _, out uint flags, out _, out IMFSample? sample);
            if (hr != Ok || (flags & (ReadFlagError | ReadFlagEndOfStream)) != 0)
            {
                Release(sample);
                return null;
            }

            if (sample is null)
            {
                // A stream tick or a gap; the next call brings the frame.
                continue;
            }

            try
            {
                return CopyFrame(reader, sample, nativeWidth, nativeHeight, maxWidth);
            }
            finally
            {
                Release(sample);
            }
        }

        return null;
    }

    /// <summary>Positions the reader a little way in, where a video has usually left its title card behind.</summary>
    private static void SeekEarly(IMFSourceReader reader, TimeSpan? duration)
    {
        if (duration is not { Ticks: > 0 } total)
        {
            return;
        }

        long target = Math.Min((long)(total.Ticks * SeekFraction), MaxSeek.Ticks);
        if (target <= 0)
        {
            return;
        }

        Guid timeFormat = Guid.Empty;
        var position = new PropVariant { Type = VtI8, Int64 = target };

        // A failed seek is not a failed probe; the first frame is fine too.
        reader.SetCurrentPosition(ref timeFormat, ref position);
    }

    private static VideoFrame? CopyFrame(IMFSourceReader reader, IMFSample sample, int nativeWidth, int nativeHeight, int maxWidth)
    {
        // The output type is read back after the sample because the reader may have changed it
        // (MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) while negotiating the converter.
        if (reader.GetCurrentMediaType(FirstVideoStream, out IMFMediaType current) != Ok)
        {
            return null;
        }

        int width;
        int height;
        int declaredStride;
        Aperture picture;
        try
        {
            (width, height) = FrameSizeOf(current);
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                return null;
            }

            Guid key = DefaultStride;
            declaredStride = current.GetUINT32(ref key, out uint stride) == Ok ? unchecked((int)stride) : 0;
            picture = ApertureOf(current, width, height, nativeWidth, nativeHeight);
        }
        finally
        {
            Release(current);
        }

        if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) != Ok)
        {
            return null;
        }

        try
        {
            byte[]? full = buffer is IMF2DBuffer planar
                ? CopyPlanar(planar, width, height)
                : CopyLinear(buffer, width, height, declaredStride);

            if (full is null)
            {
                return null;
            }

            byte[] cropped = Crop(full, width, picture);
            try
            {
                return Reduce(cropped, picture.Width, picture.Height, maxWidth);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(full);
                if (!ReferenceEquals(cropped, full))
                {
                    CryptographicOperations.ZeroMemory(cropped);
                }
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    /// <summary>The part of a decoded frame that is picture, as opposed to the decoder's padding.</summary>
    private readonly record struct Aperture(int X, int Y, int Width, int Height);

    /// <summary>
    /// H.264 and friends decode to whole macroblocks, so a 160 x 90 clip comes out as a 160 x 96 frame with
    /// six rows of padding. The output type's <c>MF_MT_MINIMUM_DISPLAY_APERTURE</c> (an <c>MFVideoArea</c>:
    /// two <c>MFOffset</c>s of fraction and value, then a <c>SIZE</c>) says where the picture is; without
    /// it, the native type's frame size is the best answer and the padding is at the bottom and right.
    /// </summary>
    private static unsafe Aperture ApertureOf(IMFMediaType type, int width, int height, int nativeWidth, int nativeHeight)
    {
        Guid key = MinimumDisplayAperture;
        byte* blob = stackalloc byte[16];
        if (type.GetBlob(ref key, (IntPtr)blob, 16, out uint written) == Ok && written == 16)
        {
            int x = *(short*)(blob + 2);
            int y = *(short*)(blob + 6);
            int w = *(int*)(blob + 8);
            int h = *(int*)(blob + 12);
            if (x >= 0 && y >= 0 && w > 0 && h > 0 && x + (long)w <= width && y + (long)h <= height)
            {
                return new Aperture(x, y, w, h);
            }
        }

        int pictureWidth = nativeWidth > 0 ? Math.Min(width, nativeWidth) : width;
        int pictureHeight = nativeHeight > 0 ? Math.Min(height, nativeHeight) : height;
        return new Aperture(0, 0, pictureWidth, pictureHeight);
    }

    /// <summary>Cuts the picture out of a padded frame; returns the input itself when nothing needs cutting.</summary>
    private static byte[] Crop(byte[] pixels, int width, Aperture picture)
    {
        if (picture.X == 0 && picture.Y == 0 && picture.Width == width && (long)picture.Height * width * 4 == pixels.Length)
        {
            return pixels;
        }

        int rowBytes = picture.Width * 4;
        byte[] cropped = new byte[rowBytes * picture.Height];
        for (int y = 0; y < picture.Height; y++)
        {
            int source = (((picture.Y + y) * width) + picture.X) * 4;
            pixels.AsSpan(source, rowBytes).CopyTo(cropped.AsSpan(y * rowBytes, rowBytes));
        }

        return cropped;
    }

    /// <summary>Copies rows through the 2-D view, whose pitch sign says whether the image is stored bottom-up.</summary>
    private static unsafe byte[]? CopyPlanar(IMF2DBuffer planar, int width, int height)
    {
        if (planar.Lock2D(out IntPtr scanline0, out int pitch) != Ok)
        {
            return null;
        }

        try
        {
            int rowBytes = width * 4;
            if (Math.Abs(pitch) < rowBytes)
            {
                return null;
            }

            byte[] pixels = new byte[rowBytes * height];
            byte* row = (byte*)scanline0;
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(row, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
                row += pitch;
            }

            return pixels;
        }
        finally
        {
            planar.Unlock2D();
        }
    }

    /// <summary>Copies rows out of a flat buffer, trusting the type's stride and treating a negative one as bottom-up.</summary>
    private static unsafe byte[]? CopyLinear(IMFMediaBuffer buffer, int width, int height, int declaredStride)
    {
        if (buffer.Lock(out IntPtr data, out _, out uint currentLength) != Ok)
        {
            return null;
        }

        try
        {
            int rowBytes = width * 4;
            int stride = declaredStride == 0 ? rowBytes : Math.Abs(declaredStride);
            if (stride < rowBytes || (long)stride * height > currentLength)
            {
                return null;
            }

            bool bottomUp = declaredStride < 0;
            byte[] pixels = new byte[rowBytes * height];
            byte* start = (byte*)data;
            for (int y = 0; y < height; y++)
            {
                int sourceRow = bottomUp ? height - 1 - y : y;
                new ReadOnlySpan<byte>(start + ((long)sourceRow * stride), rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }

            return pixels;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>
    /// Reduces a frame to at most <paramref name="maxWidth"/> pixels wide by taking every n-th pixel. The
    /// pane draws the result smaller than a video frame anyway, and a whole-number step keeps the copy
    /// cheap and the plaintext footprint small.
    /// </summary>
    /// <param name="pixels">BGRA rows, top-down, tightly packed.</param>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="maxWidth">Widest the result may be.</param>
    public static VideoFrame Reduce(ReadOnlySpan<byte> pixels, int width, int height, int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        if (pixels.Length < (long)width * height * 4)
        {
            throw new ArgumentException("The pixel buffer is shorter than the frame it describes.", nameof(pixels));
        }

        int step = (width + maxWidth - 1) / maxWidth;
        if (step <= 1)
        {
            return new VideoFrame(pixels[..(width * height * 4)].ToArray(), width, height);
        }

        int outWidth = (width + step - 1) / step;
        int outHeight = (height + step - 1) / step;
        byte[] reduced = new byte[outWidth * outHeight * 4];

        for (int y = 0; y < outHeight; y++)
        {
            int sourceRow = y * step * width * 4;
            int targetRow = y * outWidth * 4;
            for (int x = 0; x < outWidth; x++)
            {
                pixels.Slice(sourceRow + (x * step * 4), 4).CopyTo(reduced.AsSpan(targetRow + (x * 4), 4));
            }
        }

        return new VideoFrame(reduced, outWidth, outHeight);
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    /// <summary>
    /// The vault stream as a COM <c>IStream</c>. Media Foundation reads and seeks through this on its own
    /// threads; nothing else is supported, and the adapter never owns or disposes the stream.
    /// </summary>
    private sealed class StreamAdapter(Stream inner, CancellationToken ct) : IStream
    {
        private const int StreamType = 2; // STGTY_STREAM

        private readonly Lock _gate = new();

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            ct.ThrowIfCancellationRequested();

            int total = 0;
            lock (_gate)
            {
                while (total < cb)
                {
                    int read = inner.Read(pv, total, cb - total);
                    if (read <= 0)
                    {
                        break;
                    }

                    total += read;
                }
            }

            if (pcbRead != IntPtr.Zero)
            {
                Marshal.WriteInt32(pcbRead, total);
            }
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            ct.ThrowIfCancellationRequested();

            long position;
            lock (_gate)
            {
                position = inner.Seek(dlibMove, (SeekOrigin)dwOrigin);
            }

            if (plibNewPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(plibNewPosition, position);
            }
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            long length;
            lock (_gate)
            {
                length = inner.Length;
            }

            pstatstg = new STATSTG { type = StreamType, cbSize = length };
        }

        public void Commit(int grfCommitFlags)
        {
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();

        public void SetSize(long libNewSize) => throw new NotSupportedException();

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();

        public void Revert() => throw new NotSupportedException();

        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
