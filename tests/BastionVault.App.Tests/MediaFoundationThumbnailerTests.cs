using System.Windows.Media;
using System.Windows.Media.Imaging;
using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;

namespace BastionVault.App.Tests;

/// <summary>
/// The Media Foundation thumbnailer against a real, tiny H.264 MP4 (160 x 90, 2 s, 4 fps, colour bars
/// from ffmpeg's <c>testsrc</c>, index at the end of the file so the probe has to seek). The frame it
/// returns is compared against the eight frames ffmpeg decoded from the same file, which catches a
/// bottom-up copy, a swapped channel order and a wrong stride at once.
/// </summary>
/// <remarks>
/// Windows N without the Media Feature Pack has no Media Foundation; the decode tests return early
/// there instead of failing, and the availability property says which case the run was.
/// </remarks>
public sealed class MediaFoundationThumbnailerTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static MediaFoundationThumbnailer NewThumbnailer(MemoryLog? log = null) => new(log ?? new MemoryLog());

    /// <summary>Loads the reference strip as 8 BGRA frames of 160 x 90.</summary>
    private static List<byte[]> ReferenceFrames()
    {
        using FileStream file = File.OpenRead(Fixture("tiny-h264-frames.png"));
        var decoder = new PngBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

        Assert.Equal(160, converted.PixelWidth);
        Assert.Equal(90 * 8, converted.PixelHeight);

        byte[] all = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(all, converted.PixelWidth * 4, 0);

        var frames = new List<byte[]>(8);
        int frameBytes = 160 * 90 * 4;
        for (int i = 0; i < 8; i++)
        {
            frames.Add(all.AsSpan(i * frameBytes, frameBytes).ToArray());
        }

        return frames;
    }

    /// <summary>Mean absolute difference over the colour channels, ignoring alpha.</summary>
    private static double Distance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long sum = 0;
        int samples = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            samples += 3;
        }

        return (double)sum / samples;
    }

    [Fact]
    public async Task The_fixture_yields_its_dimensions_its_running_time_and_a_frame_that_matches_ffmpeg()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        var log = new MemoryLog();
        await using FileStream video = File.OpenRead(Fixture("tiny-h264.mp4"));

        VideoProbe? probe = await NewThumbnailer(log).ProbeAsync(video, "video/mp4", maxWidth: 160, CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal(160, probe.FrameWidth);
        Assert.Equal(90, probe.FrameHeight);
        Assert.NotNull(probe.Duration);
        Assert.InRange(probe.Duration.Value.TotalSeconds, 1.9, 2.1);

        Assert.NotNull(probe.Frame);
        Assert.Equal(160, probe.Frame.Width);
        Assert.Equal(90, probe.Frame.Height);
        Assert.Equal(160 * 90 * 4, probe.Frame.Pixels.Length);

        double closest = ReferenceFrames().Min(reference => Distance(probe.Frame.Pixels, reference));
        Assert.True(closest < 12, $"the decoded frame is {closest:F1} levels away from the nearest ffmpeg frame");
        Assert.Empty(log.Lines);
    }

    [Fact]
    public async Task The_frame_is_reduced_to_the_requested_width()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        await using FileStream video = File.OpenRead(Fixture("tiny-h264.mp4"));

        VideoProbe? probe = await NewThumbnailer().ProbeAsync(video, "video/mp4", maxWidth: 50, CancellationToken.None);

        Assert.NotNull(probe?.Frame);
        Assert.Equal(40, probe.Frame.Width);
        Assert.Equal(23, probe.Frame.Height);
        Assert.Equal(160, probe.FrameWidth);
    }

    [Fact]
    public async Task A_wrong_content_type_hint_does_not_stop_the_probe()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        await using FileStream video = File.OpenRead(Fixture("tiny-h264.mp4"));

        VideoProbe? probe = await NewThumbnailer().ProbeAsync(video, "video/x-matroska", maxWidth: 160, CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal(160, probe.FrameWidth);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_video_come_back_as_null_without_throwing()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        byte[] noise = new byte[64 * 1024];
        new Random(5).NextBytes(noise);
        var log = new MemoryLog();

        using var stream = new MemoryStream(noise);
        VideoProbe? probe = await NewThumbnailer(log).ProbeAsync(stream, "video/mp4", maxWidth: 320, CancellationToken.None);

        Assert.Null(probe);
        Assert.DoesNotContain(log.Lines, line => line.Contains("Media Foundation is not available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_stream_comes_back_as_null()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        using var stream = new MemoryStream();
        Assert.Null(await NewThumbnailer().ProbeAsync(stream, "video/mp4", maxWidth: 320, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_token_surfaces_as_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using FileStream video = File.OpenRead(Fixture("tiny-h264.mp4"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewThumbnailer().ProbeAsync(video, "video/mp4", 160, cancelled.Token));
    }

    [Fact]
    public async Task A_forward_only_stream_is_refused_up_front()
    {
        await using var forwardOnly = new ForwardOnlyStream();

        await Assert.ThrowsAsync<ArgumentException>(
            () => NewThumbnailer().ProbeAsync(forwardOnly, "video/mp4", 160, CancellationToken.None));
    }

    private sealed class ForwardOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
