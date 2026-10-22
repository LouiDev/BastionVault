using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The preview pane's audio path: an audio file is never read into memory; the seekable stream goes to
/// the prober, the figures and a waveform come back, and the waveform is dropped the moment the
/// selection moves on, like every other preview buffer.
/// </summary>
public sealed class PreviewAudioTests
{
    private readonly FakeAudioProber _prober = new();
    private readonly IVaultSession _session = Substitute.For<IVaultSession>();

    private PreviewViewModel NewPreview()
    {
        _session.OpenReadAsync(Arg.Any<EntryId>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[16])));

        return new PreviewViewModel(_session, new MemorySettings(), new FakeVideoThumbnailer(), _prober, new MemoryLog())
        {
            Debounce = TimeSpan.Zero,
        };
    }

    private static EntryItemViewModel Item(string name, long length = 8_601_234) => new(
        new EntryInfo(new EntryId(42), EntryId.Root, EntryKind.File, name, length, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, EntryState.Stored),
        "/" + name);

    [Fact]
    public async Task An_audio_file_shows_its_waveform_and_puts_the_figures_on_the_instrument_line()
    {
        float[] waveform = FakeAudioProber.SampleWaveform();
        _prober.Result = new AudioProbe(TimeSpan.FromSeconds(222), 2, 44100, 0, 320_000, waveform, TimeSpan.FromSeconds(222));
        EntryItemViewModel item = Item("song.mp3");

        PreviewViewModel preview = NewPreview();
        preview.Show(item);
        await preview.Completion;

        Assert.Equal(PreviewMode.Audio, preview.Mode);
        Assert.Same(waveform, preview.AudioWaveform);
        Assert.Null(preview.WaveformNote);
        Assert.Null(preview.Message);
        Assert.Equal($"MP3 audio · 3:42 · 44.1 kHz · stereo · 320 kb/s · {OperationViewModel.FormatBytes(item.Length)}", preview.InstrumentLine);

        Assert.Equal(1, _prober.Calls);
        Assert.Equal(["audio/mpeg"], _prober.ContentTypes);
        Assert.True(_prober.AllStreamsSeekable, "the prober must get a seekable stream");
        Assert.True(_prober.Columns[0] >= PreviewViewModel.MinWaveformColumns);
    }

    [Fact]
    public async Task An_uncompressed_file_adds_its_sample_width()
    {
        _prober.Result = new AudioProbe(TimeSpan.FromSeconds(30), 1, 48000, 16, 768_000, FakeAudioProber.SampleWaveform(), TimeSpan.FromSeconds(30));
        EntryItemViewModel item = Item("take.wav", 2_880_044);

        PreviewViewModel preview = NewPreview();
        preview.Show(item);
        await preview.Completion;

        Assert.Equal($"WAV audio · 0:30 · 48 kHz · 16-bit · mono · 768 kb/s · {OperationViewModel.FormatBytes(item.Length)}", preview.InstrumentLine);
    }

    [Fact]
    public async Task An_unrecognised_container_says_so_and_points_at_export()
    {
        _prober.Result = null;
        EntryItemViewModel item = Item("song.mp3");

        PreviewViewModel preview = NewPreview();
        preview.Show(item);
        await preview.Completion;

        Assert.Equal(PreviewMode.Audio, preview.Mode);
        Assert.Null(preview.AudioWaveform);
        Assert.Contains("Export", preview.Message, StringComparison.Ordinal);
        Assert.Equal($"MP3 audio · {OperationViewModel.FormatBytes(item.Length)}", preview.InstrumentLine);
    }

    [Fact]
    public async Task A_recognised_file_without_a_decoder_keeps_the_figures_and_explains_the_missing_waveform()
    {
        _prober.Result = new AudioProbe(TimeSpan.FromMinutes(4), 2, 48000, 0, 128_000, null, TimeSpan.Zero);

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("clip.opus"));
        await preview.Completion;

        Assert.Equal(PreviewMode.Audio, preview.Mode);
        Assert.Null(preview.AudioWaveform);
        Assert.Contains("decoder", preview.Message, StringComparison.Ordinal);
        Assert.Contains("4:00 · 48 kHz · stereo · 128 kb/s", preview.InstrumentLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_recording_says_how_much_of_it_the_waveform_covers()
    {
        _prober.Result = new AudioProbe(TimeSpan.FromHours(1), 1, 22050, 0, 64_000, FakeAudioProber.SampleWaveform(), TimeSpan.FromMinutes(10));

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("lecture.m4a"));
        await preview.Completion;

        Assert.Equal("Waveform of the first 10:00", preview.WaveformNote);
        Assert.Contains("1:00:00 · 22.05 kHz · mono", preview.InstrumentLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moving_the_selection_on_zeroes_the_waveform()
    {
        float[] waveform = FakeAudioProber.SampleWaveform();
        _prober.Result = new AudioProbe(TimeSpan.FromSeconds(1), 1, 8000, 16, null, waveform, TimeSpan.FromSeconds(1));

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("beep.wav"));
        await preview.Completion;
        Assert.Same(waveform, preview.AudioWaveform);

        preview.Show(null);

        Assert.Null(preview.AudioWaveform);
        Assert.Null(preview.WaveformNote);
        Assert.Equal(PreviewMode.Empty, preview.Mode);
        Assert.All(waveform, v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task Locking_zeroes_the_waveform_too()
    {
        float[] waveform = FakeAudioProber.SampleWaveform();
        _prober.Result = new AudioProbe(null, 1, 8000, 16, null, waveform, TimeSpan.FromSeconds(1));

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("beep.wav"));
        await preview.Completion;

        preview.Clear();

        Assert.Null(preview.AudioWaveform);
        Assert.All(waveform, v => Assert.Equal(0f, v));
    }

    [Fact]
    public async Task A_probe_still_running_when_the_selection_moves_is_cancelled_and_leaves_nothing_behind()
    {
        _prober.Result = new AudioProbe(TimeSpan.FromSeconds(1), 1, 8000, 16, null, FakeAudioProber.SampleWaveform(), TimeSpan.FromSeconds(1));
        _prober.Hold = new TaskCompletionSource();

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("beep.wav"));
        Task first = preview.Completion;
        Assert.Equal(PreviewMode.Loading, preview.Mode);

        preview.Show(Item("notes.txt", 16));
        _prober.Hold.SetResult();
        await first;
        await preview.Completion;

        // The substitute session serves sixteen zero bytes for every file, which the pane shows as a hex dump.
        Assert.Equal(PreviewMode.Hex, preview.Mode);
        Assert.Null(preview.AudioWaveform);
    }

    [Fact]
    public async Task A_waveform_is_blurred_when_the_window_is_inactive()
    {
        _prober.Result = new AudioProbe(TimeSpan.FromSeconds(1), 1, 8000, 16, null, FakeAudioProber.SampleWaveform(), TimeSpan.FromSeconds(1));

        PreviewViewModel preview = NewPreview();
        preview.Show(Item("beep.wav"));
        await preview.Completion;

        Assert.False(preview.IsBlurred);
        preview.IsWindowActive = false;
        Assert.True(preview.IsBlurred);
    }

    [Theory]
    [InlineData(44100, "44.1 kHz")]
    [InlineData(48000, "48 kHz")]
    [InlineData(22050, "22.05 kHz")]
    [InlineData(8000, "8 kHz")]
    [InlineData(96000, "96 kHz")]
    public void Sample_rates_read_in_kilohertz_with_only_the_decimals_they_need(int rate, string expected)
    {
        Assert.Equal(expected, PreviewViewModel.FormatSampleRate(rate));
    }

    [Theory]
    [InlineData(1, "mono")]
    [InlineData(2, "stereo")]
    [InlineData(6, "6 channels")]
    public void Channel_counts_read_like_a_player_shows_them(int channels, string expected)
    {
        Assert.Equal(expected, PreviewViewModel.FormatChannels(channels));
    }

    [Fact]
    public void The_waveform_note_only_appears_when_the_waveform_is_shorter_than_the_file()
    {
        float[] peaks = FakeAudioProber.SampleWaveform();

        Assert.Null(PreviewViewModel.WaveformNoteFor(null));
        Assert.Null(PreviewViewModel.WaveformNoteFor(new AudioProbe(TimeSpan.FromMinutes(3), 2, 44100, 0, null, peaks, TimeSpan.FromMinutes(3))));
        Assert.Null(PreviewViewModel.WaveformNoteFor(new AudioProbe(TimeSpan.FromMinutes(3), 2, 44100, 0, null, peaks, TimeSpan.FromSeconds(179.6))));
        Assert.Null(PreviewViewModel.WaveformNoteFor(new AudioProbe(null, 2, 44100, 0, null, peaks, TimeSpan.FromMinutes(10))));
        Assert.Null(PreviewViewModel.WaveformNoteFor(new AudioProbe(TimeSpan.FromHours(1), 2, 44100, 0, null, null, TimeSpan.Zero)));
        Assert.Equal("Waveform of the first 10:00", PreviewViewModel.WaveformNoteFor(new AudioProbe(TimeSpan.FromHours(1), 2, 44100, 0, null, peaks, TimeSpan.FromMinutes(10))));
    }
}
