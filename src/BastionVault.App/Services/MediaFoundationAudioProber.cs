using System.Runtime.InteropServices;
using static BastionVault.App.Services.MediaFoundationHelpers;
using static BastionVault.App.Services.MediaFoundationInterop;

namespace BastionVault.App.Services;

/// <summary>
/// <see cref="IAudioProber"/> over Media Foundation's source reader. The vault stream is wrapped as a COM
/// <c>IStream</c> exactly like the video thumbnailer does, the reader is asked for PCM, and the decoded
/// samples are reduced to one peak per short block as they stream past. Nothing but the figures and
/// those peaks is kept: the PCM lives in the reader's own buffers, which are released sample by sample.
/// </summary>
/// <remarks>
/// <para>
/// The bytes come from a vault, so they are attacker-controlled, and an audio demuxer plus decoder is a
/// larger parser than the format's own. The same rules as the thumbnailer apply: every <c>HRESULT</c> is
/// a decision rather than an exception, no failure escapes this class except cancellation, and a
/// machine without Media Foundation (Windows N without the Media Feature Pack) is reported once.
/// </para>
/// <para>
/// Decoding stops after <see cref="MaxWaveformDuration"/> of audio so a long recording costs a bounded
/// amount of work; the probe says how much of the file the waveform covers. The reader is synchronous
/// and runs on a thread-pool thread; the stream adapter checks the cancellation token on every read.
/// </para>
/// </remarks>
public sealed class MediaFoundationAudioProber : IAudioProber
{
    /// <summary>The waveform covers at most this much running time; the figures cover the whole file.</summary>
    public static readonly TimeSpan MaxWaveformDuration = TimeSpan.FromMinutes(10);

    /// <summary>One peak is kept per this much audio before the result is reduced to the requested columns.</summary>
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMilliseconds(10);

    /// <summary>Samples the reader may deliver without any audio (ticks, gaps) before the probe gives up.</summary>
    private const int MaxEmptySamples = 256;

    private const int Ok = 0;

    private readonly ILog _log;
    private int _unavailableReported;

    /// <summary>Creates the prober.</summary>
    /// <param name="log">Log; only failure kinds are recorded, never content.</param>
    public MediaFoundationAudioProber(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public Task<AudioProbe?> ProbeAsync(Stream audio, string? contentType, int columns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        if (!audio.CanSeek)
        {
            throw new ArgumentException("An audio probe needs a seekable stream.", nameof(audio));
        }

        return Task.Run(() => Probe(audio, contentType, columns, ct), ct);
    }

    private AudioProbe? Probe(Stream audio, string? contentType, int columns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            return ProbeCore(audio, contentType, columns, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (Interlocked.Exchange(ref _unavailableReported, 1) == 0)
            {
                _log.Warn("Media Foundation is not available on this machine; audio previews are off.", ex);
            }

            return null;
        }
        catch (Exception ex)
        {
            // A cancelled read surfaces as a COM failure; ask the token before calling it a bad file.
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            _log.Warn("An audio file could not be probed.", ex);
            return null;
        }
    }

    private AudioProbe? ProbeCore(Stream audio, string? contentType, int columns, CancellationToken ct)
    {
        if (MFStartup(ApiVersion, 0) != Ok)
        {
            return null;
        }

        IMFByteStream? byteStream = null;
        IMFSourceReader? reader = null;

        try
        {
            var adapter = new MediaFoundationStream(audio, ct);
            if (MFCreateMFByteStreamOnStream(adapter, out byteStream) != Ok)
            {
                return null;
            }

            HintContentType(byteStream, contentType);

            // Fails for a container no installed byte-stream handler recognises: not audio, as far as
            // this machine is concerned.
            if (MFCreateSourceReaderFromByteStream(byteStream, null, out reader) != Ok)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }

            reader.SetStreamSelection(AllStreams, 0);
            if (reader.SetStreamSelection(FirstAudioStream, 1) != Ok)
            {
                // A recognised container without an audio track.
                return null;
            }

            Figures native = NativeFigures(reader);
            TimeSpan? duration = Duration(reader);
            long? bitRate = BitRate(native, audio.Length, duration);

            Figures? pcm = SelectPcm(reader);
            if (pcm is not { Channels: > 0, SampleRate: > 0 } output)
            {
                // No decoder for the codec: the container was read, so the figures are still worth showing.
                return new AudioProbe(duration, native.Channels, native.SampleRate, native.BitsPerSample, bitRate, null, TimeSpan.Zero);
            }

            (float[] peaks, TimeSpan covers) = DecodePeaks(reader, output, columns, ct);
            int channels = native.Channels > 0 ? native.Channels : output.Channels;
            int sampleRate = native.SampleRate > 0 ? native.SampleRate : output.SampleRate;
            return new AudioProbe(duration, channels, sampleRate, native.BitsPerSample, bitRate, peaks, covers);
        }
        finally
        {
            Release(reader);
            Release(byteStream);
            MFShutdown();
        }
    }

    /// <summary>What a media type says about its audio, with zero for anything it leaves out.</summary>
    private readonly record struct Figures(int Channels, int SampleRate, int BitsPerSample, int AvgBytesPerSecond, bool IsFloat);

    private static Figures NativeFigures(IMFSourceReader reader)
    {
        if (reader.GetNativeMediaType(FirstAudioStream, 0, out IMFMediaType native) != Ok)
        {
            return default;
        }

        try
        {
            return FiguresOf(native);
        }
        finally
        {
            Release(native);
        }
    }

    private static Figures FiguresOf(IMFMediaType type)
    {
        Guid key = Subtype;
        bool isFloat = type.GetGUID(ref key, out Guid subtype) == Ok && subtype == AudioFormatFloat;
        return new Figures(
            Uint(type, AudioNumChannels),
            Uint(type, AudioSamplesPerSecond),
            Uint(type, AudioBitsPerSample),
            Uint(type, AudioAvgBytesPerSecond),
            isFloat);
    }

    private static int Uint(IMFMediaType type, Guid attribute)
    {
        Guid key = attribute;
        return type.GetUINT32(ref key, out uint value) == Ok && value <= int.MaxValue ? (int)value : 0;
    }

    /// <summary>
    /// The bit rate the container declares, or the one the file length and running time imply. A
    /// compressed stream's native type carries the average byte rate; a WAV's follows from its format.
    /// </summary>
    private static long? BitRate(Figures native, long length, TimeSpan? duration)
    {
        if (native.AvgBytesPerSecond > 0)
        {
            return native.AvgBytesPerSecond * 8L;
        }

        if (duration is { TotalSeconds: > 0.5 } total && length > 0)
        {
            return (long)Math.Round(length * 8 / total.TotalSeconds);
        }

        return null;
    }

    /// <summary>
    /// Asks the reader for integer PCM, then for float PCM, and reads back whatever it settled on. Both
    /// fail when no decoder for the codec is installed; the figures are reported without a waveform then.
    /// </summary>
    private static Figures? SelectPcm(IMFSourceReader reader)
    {
        foreach (Guid subtype in new[] { AudioFormatPcm, AudioFormatFloat })
        {
            if (MFCreateMediaType(out IMFMediaType wanted) != Ok)
            {
                return null;
            }

            int hr;
            try
            {
                Guid key = MajorType;
                Guid value = MediaTypeAudio;
                wanted.SetGUID(ref key, ref value);
                key = Subtype;
                value = subtype;
                wanted.SetGUID(ref key, ref value);
                hr = reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, wanted);
            }
            finally
            {
                Release(wanted);
            }

            if (hr != Ok)
            {
                continue;
            }

            if (reader.GetCurrentMediaType(FirstAudioStream, out IMFMediaType current) != Ok)
            {
                return null;
            }

            try
            {
                Figures output = FiguresOf(current);
                return output.BitsPerSample is 8 or 16 or 24 or 32 ? output : null;
            }
            finally
            {
                Release(current);
            }
        }

        return null;
    }

    /// <summary>
    /// Streams decoded samples past and keeps the loudest absolute sample of every block, then reduces the
    /// blocks to the requested number of columns. Stops at <see cref="MaxWaveformDuration"/>.
    /// </summary>
    private static (float[] Peaks, TimeSpan Covers) DecodePeaks(IMFSourceReader reader, Figures output, int columns, CancellationToken ct)
    {
        int frameBytes = output.Channels * (output.BitsPerSample / 8);
        int framesPerBlock = Math.Max(1, (int)(output.SampleRate * BlockDuration.TotalSeconds));
        int maxBlocks = (int)(MaxWaveformDuration.Ticks / BlockDuration.Ticks);

        var blocks = new List<float>(Math.Min(maxBlocks, 4096));
        float blockPeak = 0;
        int framesInBlock = 0;
        int emptySamples = 0;

        while (blocks.Count < maxBlocks)
        {
            ct.ThrowIfCancellationRequested();

            int hr = reader.ReadSample(FirstAudioStream, 0, out _, out uint flags, out _, out IMFSample? sample);
            if (hr != Ok || (flags & (ReadFlagError | ReadFlagEndOfStream)) != 0)
            {
                Release(sample);
                break;
            }

            if (sample is null)
            {
                if (++emptySamples > MaxEmptySamples)
                {
                    break;
                }

                continue;
            }

            try
            {
                if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) != Ok)
                {
                    break;
                }

                try
                {
                    if (!Accumulate(buffer, output, frameBytes, framesPerBlock, blocks, maxBlocks, ref blockPeak, ref framesInBlock))
                    {
                        break;
                    }
                }
                finally
                {
                    Release(buffer);
                }
            }
            finally
            {
                Release(sample);
            }
        }

        if (framesInBlock > 0 && blocks.Count < maxBlocks)
        {
            blocks.Add(blockPeak);
        }

        TimeSpan covers = TimeSpan.FromTicks(blocks.Count * BlockDuration.Ticks);
        return (Reduce(blocks, columns), covers);
    }

    /// <summary>Folds one buffer of interleaved PCM into the running blocks; false once the block cap is hit.</summary>
    private static unsafe bool Accumulate(
        IMFMediaBuffer buffer,
        Figures output,
        int frameBytes,
        int framesPerBlock,
        List<float> blocks,
        int maxBlocks,
        ref float blockPeak,
        ref int framesInBlock)
    {
        if (buffer.Lock(out IntPtr data, out _, out uint currentLength) != Ok)
        {
            return false;
        }

        try
        {
            int frames = (int)(currentLength / (uint)frameBytes);
            byte* cursor = (byte*)data;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int channel = 0; channel < output.Channels; channel++)
                {
                    float amplitude = Math.Abs(SampleAt(cursor, output.BitsPerSample, output.IsFloat));
                    if (amplitude > blockPeak)
                    {
                        blockPeak = amplitude;
                    }

                    cursor += output.BitsPerSample / 8;
                }

                if (++framesInBlock >= framesPerBlock)
                {
                    blocks.Add(Math.Min(blockPeak, 1f));
                    blockPeak = 0;
                    framesInBlock = 0;
                    if (blocks.Count >= maxBlocks)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>One sample as a value in [-1, 1], whatever width the decoder chose.</summary>
    private static unsafe float SampleAt(byte* p, int bits, bool isFloat) => bits switch
    {
        8 => (p[0] - 128) / 128f,
        16 => *(short*)p / 32768f,
        24 => ((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) / 2147483648f,
        32 when isFloat => *(float*)p,
        32 => *(int*)p / 2147483648f,
        _ => 0f,
    };

    /// <summary>
    /// Reduces per-block peaks to <paramref name="columns"/> values by taking the loudest block in each
    /// group. Fewer blocks than columns come back as they are: the view stretches them.
    /// </summary>
    /// <param name="blocks">Peak per block, in order.</param>
    /// <param name="columns">How many values the view can draw.</param>
    public static float[] Reduce(IReadOnlyList<float> blocks, int columns)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);

        if (blocks.Count <= columns)
        {
            return [.. blocks];
        }

        float[] reduced = new float[columns];
        for (int column = 0; column < columns; column++)
        {
            int start = (int)((long)column * blocks.Count / columns);
            int end = (int)((long)(column + 1) * blocks.Count / columns);
            float peak = 0;
            for (int i = start; i < end; i++)
            {
                if (blocks[i] > peak)
                {
                    peak = blocks[i];
                }
            }

            reduced[column] = peak;
        }

        return reduced;
    }
}
