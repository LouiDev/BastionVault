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
/// <see cref="IVaultSession.OpenReadAsync"/> - never to a temporary file - shows text, an image or
/// a hex dump, and drops every buffer the moment the selection changes or the vault locks
/// (UI-CONTRACT.md section 1.10). Reads are debounced so arrowing down a long list does not start
/// a decrypt per row, and an in-flight read is cancelled when the selection moves on.
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
    private readonly ILog _log;

    private CancellationTokenSource? _pending;
    private byte[]? _buffer;
    private EntryId? _showing;

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
    private string? _message;

    [ObservableProperty]
    private int _decodeWidth = 320;

    [ObservableProperty]
    private bool _isWindowActive = true;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Creates the pane over a session.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="settings">Application settings; the blur-when-inactive switch lives there.</param>
    /// <param name="log">Log.</param>
    public PreviewViewModel(IVaultSession session, ISettingsService settings, ILog log)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        _session = session;
        _settings = settings;
        _log = log;
        IsEnabled = settings.Current.PreviewEnabled;
    }

    /// <summary>True when the pane should be blurred because the window is not the active one.</summary>
    public bool IsBlurred => !IsWindowActive && _settings.Current.BlurPreviewWhenInactive && Mode is PreviewMode.Text or PreviewMode.Image or PreviewMode.Hex;

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

    /// <summary>Formats bytes as the pane's hex dump: offset, uppercase hex in fours, ASCII.</summary>
    /// <param name="bytes">The bytes to dump.</param>
    /// <param name="totalLength">Length of the whole file, for the trailing note.</param>
    public static string FormatHexDump(ReadOnlySpan<byte> bytes, long totalLength)
    {
        var text = new StringBuilder(bytes.Length * 4);
        var ascii = new StringBuilder(16);

        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int count = Math.Min(16, bytes.Length - offset);
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            ascii.Clear();

            for (int i = 0; i < 16; i++)
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

        int dump = Math.Min(bytes.Length, HexDumpBytes);
        Text = FormatHexDump(bytes.AsSpan(0, dump), item.Length);
        Mode = PreviewMode.Hex;
        OnPropertyChanged(nameof(IsBlurred));
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

        Text = null;
    }

    partial void OnIsWindowActiveChanged(bool value) => OnPropertyChanged(nameof(IsBlurred));

    partial void OnModeChanged(PreviewMode value) => OnPropertyChanged(nameof(IsBlurred));
}
