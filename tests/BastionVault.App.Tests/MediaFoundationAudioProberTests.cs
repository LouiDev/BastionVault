using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;

namespace BastionVault.App.Tests;

/// <summary>
/// The Media Foundation audio prober against WAV files built in memory, so the figures and the shape of
/// the waveform are known exactly: a second of silence, a loud burst, silence again.
/// </summary>
/// <remarks>
/// Windows N without the Media Feature Pack has no Media Foundation; the decode tests return early there
/// instead of failing, and <see cref="MediaFoundationThumbnailer.IsAvailable"/> says which case a run was.
/// </remarks>
public sealed class MediaFoundationAudioProberTests
{
    private static MediaFoundationAudioProber NewProber(MemoryLog? log = null) => new(log ?? new MemoryLog());

    /// <summary>
    /// A canonical RIFF/WAVE file: <paramref name="seconds"/> of audio at <paramref name="rate"/> Hz,
    /// <paramref name="channels"/> channels, 16-bit integer or 32-bit float samples, with the amplitude of
    /// every sample given by <paramref name="envelope"/> as a function of time in seconds.
    /// </summary>
    private static byte[] Wav(int rate, int channels, bool isFloat, double seconds, Func<double, double> envelope)
    {
        int bits = isFloat ? 32 : 16;
        int frames = (int)(rate * seconds);
        int blockAlign = channels * bits / 8;
        int dataBytes = frames * blockAlign;

        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)(isFloat ? 3 : 1));
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bits);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            double value = envelope(t) * Math.Sin(2 * Math.PI * 440 * t);
            for (int c = 0; c < channels; c++)
            {
                if (isFloat)
                {
                    writer.Write((float)value);
                }
                else
                {
                    writer.Write((short)Math.Round(value * short.MaxValue));
                }
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Silent for the first and last third, full scale in the middle.</summary>
    private static double Burst(double t) => t is >= 1 and < 2 ? 1 : 0;

    [Fact]
    public async Task A_wav_file_yields_its_figures_and_a_waveform_shaped_like_the_audio()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        byte[] wav = Wav(8000, 1, isFloat: false, seconds: 3, Burst);
        using var stream = new MemoryStream(wav);

        AudioProbe? probe = await NewProber().ProbeAsync(stream, "audio/wav", columns: 30, CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(8000, probe.SampleRate);
        Assert.Equal(16, probe.BitsPerSample);
        Assert.Equal(8000 * 2 * 8, probe.BitRate);
        Assert.NotNull(probe.Duration);
        Assert.InRange(probe.Duration.Value.TotalSeconds, 2.9, 3.1);
        Assert.InRange(probe.WaveformCovers.TotalSeconds, 2.9, 3.1);

        Assert.NotNull(probe.Waveform);
        Assert.Equal(30, probe.Waveform.Length);
        Assert.All(probe.Waveform.Take(9), v => Assert.InRange(v, 0f, 0.05f));
        Assert.All(probe.Waveform.Skip(11).Take(8), v => Assert.InRange(v, 0.9f, 1f));
        Assert.All(probe.Waveform.Skip(21), v => Assert.InRange(v, 0f, 0.05f));
    }

    [Fact]
    public async Task A_stereo_float_wav_is_decoded_as_well()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        byte[] wav = Wav(16000, 2, isFloat: true, seconds: 1.5, t => t < 0.5 ? 0.5 : 0);
        using var stream = new MemoryStream(wav);

        AudioProbe? probe = await NewProber().ProbeAsync(stream, "audio/wav", columns: 15, CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(16000, probe.SampleRate);
        Assert.NotNull(probe.Waveform);
        Assert.InRange(probe.Waveform[1], 0.45f, 0.55f);
        Assert.InRange(probe.Waveform[^1], 0f, 0.05f);
    }

    [Fact]
    public async Task Fewer_blocks_than_columns_come_back_unreduced()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        // 50 ms of audio is five 10 ms blocks; asking for 400 columns gets those five.
        byte[] wav = Wav(8000, 1, isFloat: false, seconds: 0.05, _ => 0.25);
        using var stream = new MemoryStream(wav);

        AudioProbe? probe = await NewProber().ProbeAsync(stream, "audio/wav", columns: 400, CancellationToken.None);

        Assert.NotNull(probe?.Waveform);
        Assert.InRange(probe.Waveform.Length, 4, 6);
    }

    [Fact]
    public async Task Bytes_that_are_not_audio_come_back_as_null_without_throwing()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        var log = new MemoryLog();
        byte[] junk = new byte[4096];
        new Random(7).NextBytes(junk);
        using var stream = new MemoryStream(junk);

        AudioProbe? probe = await NewProber(log).ProbeAsync(stream, "audio/mpeg", columns: 100, CancellationToken.None);

        Assert.Null(probe);
        Assert.DoesNotContain(log.Lines, line => line.Contains("not available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_probe_throws_cancellation_and_nothing_else()
    {
        byte[] wav = Wav(8000, 1, isFloat: false, seconds: 1, Burst);
        using var stream = new MemoryStream(wav);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewProber().ProbeAsync(stream, "audio/wav", columns: 10, cancellation.Token));
    }

    [Fact]
    public async Task An_unseekable_stream_is_refused_up_front()
    {
        await using var forwardOnly = new ForwardOnlyStream();

        await Assert.ThrowsAsync<ArgumentException>(
            () => NewProber().ProbeAsync(forwardOnly, "audio/wav", columns: 10, CancellationToken.None));
    }

    [Fact]
    public void Reduce_keeps_the_loudest_block_of_every_group()
    {
        float[] blocks = [0.1f, 0.9f, 0.2f, 0.3f, 0.8f, 0.1f, 0.5f, 0.4f];

        float[] reduced = MediaFoundationAudioProber.Reduce(blocks, 4);

        Assert.Equal([0.9f, 0.3f, 0.8f, 0.5f], reduced);
        Assert.Equal(blocks, MediaFoundationAudioProber.Reduce(blocks, 8));
        Assert.Equal(blocks, MediaFoundationAudioProber.Reduce(blocks, 100));
        Assert.Equal([0.9f], MediaFoundationAudioProber.Reduce(blocks, 1));
        Assert.Empty(MediaFoundationAudioProber.Reduce([], 10));
    }

    private sealed class ForwardOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
