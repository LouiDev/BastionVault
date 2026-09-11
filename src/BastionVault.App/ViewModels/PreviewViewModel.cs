using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BastionVault.App.Services;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BastionVault.App.ViewModels;

/// <summary>What the preview pane is currently showing.</summary>
public enum PreviewMode
{
    /// <summary>Nothing is selected.</summary>
    Empty,

    /// <summary>A folder is selected; the pane shows its counts instead of content.</summary>
    Folder,

    /// <summary>Bytes are being read.</summary>
    Loading,

    /// <summary>Decoded text.</summary>
    Text,

    /// <summary>A decoded image.</summary>
    Image,

    /// <summary>A still frame and the figures of a video; the frame is missing when no decoder is installed.</summary>
    Video,

    /// <summary>A hex dump of the first bytes.</summary>
    Hex,

    /// <summary>The file is larger than the pane is willing to hold in memory.</summary>
    TooLarge,

    /// <summary>The bytes could not be read or authenticated.</summary>
    Failed,

    /// <summary>The pane is switched off, or panic mode hid it.</summary>
    Hidden,
}

/// <summary>
/// The preview pane. It reads a file's plaintext into memory through
/// <see cref="IVaultSession.OpenReadAsync"/> - never to a temporary file - shows text, an image, a
/// hex dump or one still frame of a video, and drops every buffer the moment the selection changes
/// or the vault locks (UI-CONTRACT.md section 1.10). Reads are debounced so arrowing down a long list
/// does not start a decrypt per row, and an in-flight read is cancelled when the selection moves on.
/// A video is not read into memory at all: the seekable stream is handed to the
/// <see cref="IVideoThumbnailer"/>, which pulls only the bytes the container's index and one frame need.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IDisposable
{
    /// <summary>Text files are decoded up to this size; beyond it the pane says so.</summary>
    public const long MaxTextBytes = 2L * 1024 * 1024;

    /// <summary>Images are held in memory up to this size.</summary>
    public const long MaxImageBytes = 64L * 1024 * 1024;

    /// <summary>How much of a binary file the hex dump shows.</summary>
    public const int HexDumpBytes = 4 * 1024;

    private readonly IVaultSession _session;
    private readonly ISettingsService _settings;
    private readonly IVideoThumbnailer _thumbnailer;
    private readonly ILog _log;

    /// <summary>A video frame is never reduced below this width, whatever the pane's size at the time.</summary>
    public const int MinVideoFrameWidth = 640;

    /// <summary>Bytes per hex line when the pane is wide enough for the familiar layout.</summary>
    public const int WideHexBytesPerLine = 16;

    /// <summary>Bytes per hex line in a narrow pane; the ASCII column stays.</summary>
    public const int NarrowHexBytesPerLine = 8;

    private CancellationTokenSource? _pending;
    private byte[]? _buffer;
    private EntryId? _showing;
    private long _hexTotalLength;

    [ObservableProperty]
    private PreviewMode _mode = PreviewMode.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _instrumentLine = string.Empty;

    [ObservableProperty]
    private string? _text;

    [ObservableProperty]
    private byte[]? _imageBytes;

    [ObservableProperty]
    private VideoFrame? _videoFrame;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private int _decodeWidth = 320;

    [ObservableProperty]
    private int _hexBytesPerLine = WideHexBytesPerLine;

    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Creates the pane over a session.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="settings">Application settings; the blur-when-inactive switch lives there.</param>
    /// <param name="thumbnailer">Reads one frame and the figures out of a video.</param>
    /// <param name="log">Log.</param>
    public PreviewViewModel(IVaultSession session, ISettingsService settings, IVideoThumbnailer thumbnailer, ILog log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(thumbnailer);

        _session = session;
        _settings = settings;
        _thumbnailer = thumbnailer;
        _log = log;
        IsEnabled = settings.Current.PreviewEnabled;
    }

    /// <summary>True when the pane should be blurred because the window is not the active one.</summary>
    public bool IsBlurred => !IsWindowActive && _settings.Current.BlurPreviewWhenInactive && Mode is PreviewMode.Text or PreviewMode.Image or PreviewMode.Video or PreviewMode.Hex;

    /// <summary>How long the pane waits before reading, so arrowing through a list is free.</summary>
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The read that is running, for tests to await.</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows an entry, after the debounce. Passing <see langword="null"/> or calling it again
    /// cancels whatever was in flight and drops the buffers first.
    /// </summary>
    /// <param name="item">The entry to preview, or <see langword="null"/> for nothing.</param>
    public void Show(EntryItemViewModel? item)
    {
        CancelPending();
        DropBuffers();

        if (!IsEnabled)
        {
            _showing = null;
            Mode = PreviewMode.Hidden;
            Title = string.Empty;
            InstrumentLine = string.Empty;
            return;
        }

        if (item is null)
        {
            _showing = null;
            Mode = PreviewMode.Empty;
            Title = string.Empty;
            InstrumentLine = string.Empty;
            Message = null;
            return;
        }

        _showing = item.Id;
        Title = item.Name;

        if (item.IsFolder)
        {
            Mode = PreviewMode.Folder;
            InstrumentLine = string.Create(
                CultureInfo.CurrentCulture,
                $"{item.ChildCount:N0} items · {OperationViewModel.FormatBytes(item.Length)}");
            Message = null;
            return;
        }

        InstrumentLine = string.Create(
            CultureInfo.CurrentCulture,
            $"{item.TypeName} · {OperationViewModel.FormatBytes(item.Length)}");
        Mode = PreviewMode.Loading;
        Message = null;

        var cancellation = new CancellationTokenSource();
        _pending = cancellation;
        Completion = LoadAsync(item, cancellation.Token);
    }

    /// <summary>
    /// Re-reads the entry currently on show, for example after the pane was switched back on.
    /// </summary>
    /// <param name="item">The entry to show again, or <see langword="null"/> to clear.</param>
    public void Reload(EntryItemViewModel? item) => Show(item);

    /// <summary>Cancels any read and zeroes every buffer. Called on lock and on dispose.</summary>
    public void Clear()
    {
        CancelPending();
        DropBuffers();
        _showing = null;
        Mode = IsEnabled ? PreviewMode.Empty : PreviewMode.Hidden;
        Title = string.Empty;
        InstrumentLine = string.Empty;
        Message = null;
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    /// <summary>
    /// Width of one hex line in monospace columns: 8 offset digits, two spaces, two digits per byte, one
    /// space after every group of four, one space before the ASCII column, one column per byte of ASCII.
    /// </summary>
    /// <param name="bytesPerLine">Bytes on the line; a positive multiple of 4.</param>
    public static int HexLineColumns(int bytesPerLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerLine, 4);
        return 8 + 2 + (bytesPerLine * 2) + (bytesPerLine / 4) + 1 + bytesPerLine;
    }

    /// <summary>
    /// Chooses the bytes per hex line for a pane that can show <paramref name="availableColumns"/> monospace
    /// characters: 16 when the familiar layout fits, 8 otherwise, so the ASCII column is never the part that
    /// falls off the edge.
    /// </summary>
    /// <param name="availableColumns">Monospace columns the pane can show without a horizontal scrollbar.</param>
    public static int HexBytesPerLineFor(int availableColumns) =>
        availableColumns >= HexLineColumns(WideHexBytesPerLine) ? WideHexBytesPerLine : NarrowHexBytesPerLine;

    /// <summary>Formats bytes as the pane's hex dump: offset, uppercase hex in fours, ASCII, 16 bytes to a line.</summary>
    /// <param name="bytes">The bytes to dump.</param>
    /// <param name="totalLength">Length of the whole file, for the trailing note.</param>
    public static string FormatHexDump(ReadOnlySpan<byte> bytes, long totalLength) =>
        FormatHexDump(bytes, totalLength, WideHexBytesPerLine);

    /// <summary>Formats bytes as the pane's hex dump: offset, uppercase hex in fours, ASCII.</summary>
    /// <param name="bytes">The bytes to dump.</param>
    /// <param name="totalLength">Length of the whole file, for the trailing note.</param>
    /// <param name="bytesPerLine">Bytes on each line; a positive multiple of 4.</param>
    public static string FormatHexDump(ReadOnlySpan<byte> bytes, long totalLength, int bytesPerLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerLine, 4);
        if (bytesPerLine % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerLine), bytesPerLine, "Bytes per line must be a multiple of 4.");
        }

        var text = new StringBuilder(bytes.Length * 4);
        var ascii = new StringBuilder(bytesPerLine);

        for (int offset = 0; offset < bytes.Length; offset += bytesPerLine)
        {
            int count = Math.Min(bytesPerLine, bytes.Length - offset);
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            ascii.Clear();

            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i < count)
                {
                    byte value = bytes[offset + i];
                    text.Append(value.ToString("X2", CultureInfo.InvariantCulture));
                    ascii.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
                }
                else
                {
                    text.Append("  ");
                }

                if (i % 4 == 3)
                {
                    text.Append(' ');
                }
            }

            text.Append(' ').Append(ascii).Append('\n');
        }

        if (totalLength > bytes.Length)
        {
            text.Append('\n')
                .Append(CultureInfo.CurrentCulture, $"... {OperationViewModel.FormatBytes(totalLength - bytes.Length)} more");
        }

        return text.ToString();
    }

    private static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        int probe = Math.Min(bytes.Length, 512);
        for (int i = 0; i < probe; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task LoadAsync(EntryItemViewModel item, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct).ConfigureAwait(true);

            if (item.Preview == PreviewKind.Video)
            {
                await LoadVideoAsync(item, ct).ConfigureAwait(true);
                return;
            }

            long cap = item.Preview == PreviewKind.Image ? MaxImageBytes : MaxTextBytes;
            if (item.Preview != PreviewKind.Binary && item.Length > cap)
            {
                Mode = PreviewMode.TooLarge;
                Message = $"This file is {OperationViewModel.FormatBytes(item.Length)}. Export it to open it in a real viewer.";
                return;
            }

            long take = item.Preview == PreviewKind.Binary ? Math.Min(item.Length, HexDumpBytes) : item.Length;
            byte[] bytes = await Task.Run(() => ReadAsync(item.Id, take, ct), ct).ConfigureAwait(true);

            ct.ThrowIfCancellationRequested();

            if (_showing != item.Id)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return;
            }

            _buffer = bytes;
            Render(item, bytes);
        }
        catch (OperationCanceledException)
        {
            // A newer selection won; nothing to report.
        }
        catch (Exception ex) when (ex is VaultException or IOException or NotImplementedException)
        {
            _log.Warn("A preview could not be read.", ex);
            Mode = PreviewMode.Failed;
            Message = ex is VaultIntegrityException
                ? "This file failed its integrity check. Run Verify to see how much of the vault is affected."
                : "This file could not be read.";
        }
    }

    /// <summary>
    /// Probes a video through the thumbnailer. The whole file is never held: the stream is seekable and
    /// the media stack reads the container index and the one frame it needs. There is no size cap for
    /// the same reason.
    /// </summary>
    private async Task LoadVideoAsync(EntryItemViewModel item, CancellationToken ct)
    {
        int maxWidth = Math.Max(DecodeWidth, MinVideoFrameWidth);
        VideoProbe? probe = await Task.Run(
            async () =>
            {
                await using Stream stream = await _session.OpenReadAsync(item.Id, ct).ConfigureAwait(false);
                return await _thumbnailer.ProbeAsync(stream, item.ContentType, maxWidth, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(true);

        ct.ThrowIfCancellationRequested();

        if (_showing != item.Id)
        {
            ZeroFrame(probe?.Frame);
            return;
        }

        InstrumentLine = VideoInstrumentLine(item, probe);
        VideoFrame = probe?.Frame;
        Message = probe switch
        {
            null => "No preview for this video. Export it to play it.",
            { Frame: null } => "No decoder for this video format is installed. Export it to play it.",
            _ => null,
        };
        Mode = PreviewMode.Video;
        OnPropertyChanged(nameof(IsBlurred));
    }

    /// <summary>"MP4 video · 1920×1080 · 12:34 · 512 MB", leaving out whatever the probe did not learn.</summary>
    /// <param name="item">The entry on show.</param>
    /// <param name="probe">What the thumbnailer found, or <see langword="null"/>.</param>
    public static string VideoInstrumentLine(EntryItemViewModel item, VideoProbe? probe)
    {
        ArgumentNullException.ThrowIfNull(item);

        var parts = new List<string>(4) { item.TypeName };
        if (probe is { FrameWidth: > 0, FrameHeight: > 0 })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{probe.FrameWidth}×{probe.FrameHeight}"));
        }

        if (probe?.Duration is { } duration)
        {
            parts.Add(FormatDuration(duration));
        }

        parts.Add(OperationViewModel.FormatBytes(item.Length));
        return string.Join(" · ", parts);
    }

    /// <summary>Minutes and seconds, with hours in front once there are any: "0:07", "12:34", "1:02:03".</summary>
    /// <param name="duration">The running time; negative values are shown as zero.</param>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        long totalSeconds = (long)Math.Round(duration.TotalSeconds);
        long hours = totalSeconds / 3600;
        long minutes = (totalSeconds % 3600) / 60;
        long seconds = totalSeconds % 60;

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    private static void ZeroFrame(VideoFrame? frame)
    {
        if (frame is not null)
        {
            CryptographicOperations.ZeroMemory(frame.Pixels);
        }
    }

    private void Render(EntryItemViewModel item, byte[] bytes)
    {
        if (item.Preview == PreviewKind.Image)
        {
            ImageBytes = bytes;
            Mode = PreviewMode.Image;
            OnPropertyChanged(nameof(IsBlurred));
            return;
        }

        if (item.Preview == PreviewKind.Text && !LooksBinary(bytes))
        {
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                ReadOnlySpan<byte> span = bytes;
                if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                {
                    span = span[3..];
                }

                Text = strict.GetString(span);
                Mode = PreviewMode.Text;
                OnPropertyChanged(nameof(IsBlurred));
                return;
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8 after all; the hex dump below is the honest answer.
            }
        }

        _hexTotalLength = item.Length;
        Text = FormatHexDump(bytes.AsSpan(0, Math.Min(bytes.Length, HexDumpBytes)), item.Length, HexBytesPerLine);
        Mode = PreviewMode.Hex;
        OnPropertyChanged(nameof(IsBlurred));
    }

    /// <summary>
    /// Re-renders the dump on show when the pane changed width. The bytes are the ones already held for the
    /// current entry, so nothing is decrypted again and nothing new is kept.
    /// </summary>
    /// <param name="value">The new bytes-per-line figure.</param>
    partial void OnHexBytesPerLineChanged(int value)
    {
        if (Mode == PreviewMode.Hex && _buffer is { } bytes)
        {
            Text = FormatHexDump(bytes.AsSpan(0, Math.Min(bytes.Length, HexDumpBytes)), _hexTotalLength, value);
        }
    }

    private async Task<byte[]> ReadAsync(EntryId id, long take, CancellationToken ct)
    {
        await using Stream stream = await _session.OpenReadAsync(id, ct).ConfigureAwait(false);

        // The exact byte count is known up front, so read straight into the array that is handed
        // out. A MemoryStream would hold a second, untracked copy of the plaintext (plus every
        // array it abandoned while growing) that DropBuffers could never zero, and above 85 KB
        // that copy lands on the large object heap where it survives for a long time.
        int capacity = (int)Math.Clamp(take, 0, int.MaxValue);
        byte[] buffer = capacity == 0 ? [] : new byte[capacity];
        int copied = 0;

        while (copied < capacity)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(copied, capacity - copied), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            copied += read;
        }

        if (copied == capacity)
        {
            return buffer;
        }

        // The stream ended early. Hand out a right-sized array and zero the one that held the
        // plaintext, so the short read leaves no untracked copy behind either.
        byte[] trimmed = new byte[copied];
        Array.Copy(buffer, trimmed, copied);
        CryptographicOperations.ZeroMemory(buffer);
        return trimmed;
    }

    private void CancelPending()
    {
        CancellationTokenSource? pending = _pending;
        _pending = null;
        if (pending is null)
        {
            return;
        }

        pending.Cancel();
        pending.Dispose();
    }

    private void DropBuffers()
    {
        if (_buffer is not null)
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = null;
        }

        if (ImageBytes is not null)
        {
            ImageBytes = null;
        }

        if (VideoFrame is not null)
        {
            ZeroFrame(VideoFrame);
            VideoFrame = null;
        }

        Text = null;
    }

    partial void OnIsWindowActiveChanged(bool value) => OnPropertyChanged(nameof(IsBlurred));

    partial void OnModeChanged(PreviewMode value) => OnPropertyChanged(nameof(IsBlurred));
}
