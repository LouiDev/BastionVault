using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.Core;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The preview pane's video path (#33): a video is never read into memory; the seekable stream goes
/// to the thumbnailer, one frame and the figures come back, and the frame is zeroed the moment the
/// selection moves on, like every other preview buffer.
/// </summary>
public sealed class PreviewVideoTests : IDisposable
{
    private readonly ExplorerTestContext _context = new();

    public void Dispose() => _context.Dispose();

    private PreviewViewModel Preview
    {
        get
        {
            PreviewViewModel preview = _context.Explorer.Preview;
            preview.Debounce = TimeSpan.Zero;
            return preview;
        }
    }

    private EntryItemViewModel Item(string name)
    {
        EntryInfo info = Find(EntryId.Root, name) ?? throw new InvalidOperationException($"{name} is not in the demo vault");
        return new EntryItemViewModel(info, "/" + name);
    }

    private EntryInfo? Find(EntryId parent, string name)
    {
        foreach (EntryInfo child in _context.Session.GetChildren(parent))
        {
            if (child.Name == name)
            {
                return child;
            }

            if (child.Kind == EntryKind.Folder && Find(child.Id, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public async Task A_video_shows_its_frame_and_puts_the_figures_on_the_instrument_line()
    {
        VideoFrame frame = FakeVideoThumbnailer.SampleFrame();
        _context.Thumbnailer.Result = new VideoProbe(1920, 1080, TimeSpan.FromSeconds(754), frame);
        EntryItemViewModel item = Item("Harbour at dusk.mp4");

        PreviewViewModel preview = Preview;
        preview.Show(item);
        await preview.Completion;

        Assert.Equal(PreviewMode.Video, preview.Mode);
        Assert.Same(frame, preview.VideoFrame);
        Assert.Null(preview.Message);
        Assert.Equal($"MP4 video · 1920×1080 · 12:34 · {OperationViewModel.FormatBytes(item.Length)}", preview.InstrumentLine);

        Assert.Equal(1, _context.Thumbnailer.Calls);
        Assert.Equal(["video/mp4"], _context.Thumbnailer.ContentTypes);
        Assert.True(_context.Thumbnailer.AllStreamsSeekable, "the thumbnailer must get a seekable stream");
        Assert.True(_context.Thumbnailer.MaxWidths[0] >= PreviewViewModel.MinVideoFrameWidth);
    }

    [Fact]
    public async Task An_unrecognised_container_says_so_and_points_at_export()
    {
        _context.Thumbnailer.Result = null;
        EntryItemViewModel item = Item("Harbour at dusk.mp4");

        PreviewViewModel preview = Preview;
        preview.Show(item);
        await preview.Completion;

        Assert.Equal(PreviewMode.Video, preview.Mode);
        Assert.Null(preview.VideoFrame);
        Assert.Contains("Export", preview.Message, StringComparison.Ordinal);
        Assert.Equal($"MP4 video · {OperationViewModel.FormatBytes(item.Length)}", preview.InstrumentLine);
    }

    [Fact]
    public async Task A_recognised_video_without_a_decoder_keeps_the_figures_and_explains_the_missing_picture()
    {
        _context.Thumbnailer.Result = new VideoProbe(3840, 2160, TimeSpan.FromMinutes(90), null);

        PreviewViewModel preview = Preview;
        preview.Show(Item("Harbour at dusk.mp4"));
        await preview.Completion;

        Assert.Equal(PreviewMode.Video, preview.Mode);
        Assert.Null(preview.VideoFrame);
        Assert.Contains("decoder", preview.Message, StringComparison.Ordinal);
        Assert.Contains("3840×2160 · 1:30:00", preview.InstrumentLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_the_selection_on_zeroes_the_frame()
    {
        VideoFrame frame = FakeVideoThumbnailer.SampleFrame();
        _context.Thumbnailer.Result = new VideoProbe(2, 2, TimeSpan.FromSeconds(1), frame);

        PreviewViewModel preview = Preview;
        preview.Show(Item("Harbour at dusk.mp4"));
        await preview.Completion;
        Assert.Same(frame, preview.VideoFrame);

        preview.Show(null);

        Assert.Null(preview.VideoFrame);
        Assert.Equal(PreviewMode.Empty, preview.Mode);
        Assert.All(frame.Pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Locking_zeroes_the_frame_too()
    {
        VideoFrame frame = FakeVideoThumbnailer.SampleFrame();
        _context.Thumbnailer.Result = new VideoProbe(2, 2, null, frame);

        PreviewViewModel preview = Preview;
        preview.Show(Item("Harbour at dusk.mp4"));
        await preview.Completion;

        preview.Clear();

        Assert.Null(preview.VideoFrame);
        Assert.All(frame.Pixels, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task A_probe_still_running_when_the_selection_moves_is_cancelled_and_leaves_nothing_behind()
    {
        VideoFrame frame = FakeVideoThumbnailer.SampleFrame();
        _context.Thumbnailer.Result = new VideoProbe(2, 2, null, frame);
        _context.Thumbnailer.Hold = new TaskCompletionSource();

        PreviewViewModel preview = Preview;
        preview.Show(Item("Harbour at dusk.mp4"));
        Task first = preview.Completion;
        Assert.Equal(PreviewMode.Loading, preview.Mode);

        preview.Show(Item("README.txt"));
        _context.Thumbnailer.Hold.SetResult();
        await first;
        await preview.Completion;

        // The demo session serves zero bytes for every file, which the pane shows as a hex dump.
        Assert.Equal(PreviewMode.Hex, preview.Mode);
        Assert.Null(preview.VideoFrame);
    }

    [Fact]
    public async Task A_video_frame_is_blurred_when_the_window_is_inactive()
    {
        _context.Thumbnailer.Result = new VideoProbe(2, 2, null, FakeVideoThumbnailer.SampleFrame());

        PreviewViewModel preview = Preview;
        preview.Show(Item("Harbour at dusk.mp4"));
        await preview.Completion;

        Assert.False(preview.IsBlurred);
        preview.IsWindowActive = false;
        Assert.True(preview.IsBlurred);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(59.6, "1:00")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Durations_read_like_a_player_shows_them(double seconds, string expected)
    {
        Assert.Equal(expected, PreviewViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Reduce_takes_every_nth_pixel_and_keeps_the_rows_straight()
    {
        // 8 x 4 frame whose pixel (x, y) is (B=x, G=y, R=0xAA, A=0xFF).
        byte[] pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int i = ((y * 8) + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = 0xAA;
                pixels[i + 3] = 0xFF;
            }
        }

        VideoFrame reduced = MediaFoundationThumbnailer.Reduce(pixels, 8, 4, maxWidth: 4);

        Assert.Equal(4, reduced.Width);
        Assert.Equal(2, reduced.Height);
        Assert.Equal(4 * 2 * 4, reduced.Pixels.Length);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int i = ((y * 4) + x) * 4;
                Assert.Equal((byte)(x * 2), reduced.Pixels[i]);
                Assert.Equal((byte)(y * 2), reduced.Pixels[i + 1]);
                Assert.Equal(0xAA, reduced.Pixels[i + 2]);
            }
        }

        VideoFrame untouched = MediaFoundationThumbnailer.Reduce(pixels, 8, 4, maxWidth: 8);
        Assert.Equal(8, untouched.Width);
        Assert.Equal(4, untouched.Height);
        Assert.Equal(pixels, untouched.Pixels);
        Assert.NotSame(pixels, untouched.Pixels);
    }
}
