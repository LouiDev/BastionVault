using Microsoft.Win32.SafeHandles;

namespace BastionVault.Core.Session;

/// <summary>
/// Reads the ciphertext bytes of one blob. Implementations are the only code in the session layer
/// that touches blob bytes; everything above them works on plaintext or on whole chunks.
/// </summary>
internal interface IBlobSource
{
    /// <summary>Ciphertext length of the blob in bytes (plaintext length plus 16 bytes per chunk).</summary>
    long Length { get; }

    /// <summary>Reads ciphertext bytes at a blob-relative offset, filling the destination completely.</summary>
    /// <param name="offset">Offset inside the blob.</param>
    /// <param name="destination">Buffer to fill.</param>
    void Read(long offset, Span<byte> destination);
}

/// <summary>A blob that lives inside the open vault file at a fixed absolute offset.</summary>
internal sealed class StoredBlobSource : IBlobSource
{
    private readonly VaultFileHandle _file;
    private readonly long _fileOffset;

    /// <summary>Binds a blob to a byte range of the open vault file.</summary>
    /// <param name="file">The session's vault file handle.</param>
    /// <param name="fileOffset">Absolute offset of the blob in the file.</param>
    /// <param name="length">Ciphertext length of the blob.</param>
    public StoredBlobSource(VaultFileHandle file, long fileOffset, long length)
    {
        _file = file;
        _fileOffset = fileOffset;
        Length = length;
    }

    /// <inheritdoc />
    public long Length { get; }

    /// <summary>Absolute offset of the blob inside the vault file.</summary>
    public long FileOffset => _fileOffset;

    /// <inheritdoc />
    public void Read(long offset, Span<byte> destination)
    {
        RequireRange(offset, destination.Length, Length);
        try
        {
            FileIo.ReadExactly(_file.Handle, _fileOffset + offset, destination);
        }
        catch (ObjectDisposedException ex)
        {
            throw new VaultIoException(VaultErrorCode.IoError, "The vault file was closed while it was being read.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw IoGuard.Translate(ex, null);
        }
    }

    /// <summary>Rejects a read that would leave the blob.</summary>
    /// <param name="offset">Blob-relative offset.</param>
    /// <param name="count">Number of bytes requested.</param>
    /// <param name="length">Length of the blob.</param>
    internal static void RequireRange(long offset, int count, long length)
    {
        if (offset < 0 || count < 0 || offset + count > length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"A read of {count} bytes at offset {offset} leaves a blob of {length} bytes.");
        }
    }
}

/// <summary>A blob whose ciphertext is held in a managed buffer (staging below the in-memory limit).</summary>
internal sealed class MemoryBlobSource : IBlobSource
{
    private readonly byte[] _ciphertext;

    /// <summary>Wraps a ciphertext buffer.</summary>
    /// <param name="ciphertext">The complete blob ciphertext; the instance takes ownership.</param>
    public MemoryBlobSource(byte[] ciphertext) => _ciphertext = ciphertext;

    /// <inheritdoc />
    public long Length => _ciphertext.Length;

    /// <summary>The whole ciphertext, for callers that copy it somewhere else.</summary>
    internal ReadOnlySpan<byte> Bytes => _ciphertext;

    /// <inheritdoc />
    public void Read(long offset, Span<byte> destination)
    {
        StoredBlobSource.RequireRange(offset, destination.Length, _ciphertext.Length);
        _ciphertext.AsSpan((int)offset, destination.Length).CopyTo(destination);
    }
}

/// <summary>
/// The vault file handle of a session. It is replaced (not recreated) when a save reopens the file,
/// so every <see cref="StoredBlobSource"/> keeps working without being rebound.
/// </summary>
internal sealed class VaultFileHandle : IDisposable
{
    private SafeFileHandle? _handle;

    /// <summary>Wraps the handle a session was opened with.</summary>
    /// <param name="handle">An open, readable handle to the vault file.</param>
    public VaultFileHandle(SafeFileHandle handle) => _handle = handle;

    /// <summary>The current handle.</summary>
    /// <exception cref="VaultIoException">The handle is closed (the session is disposed or mid-save).</exception>
    public SafeFileHandle Handle =>
        _handle ?? throw new VaultIoException(VaultErrorCode.IoError, "The vault file is not open.");

    /// <summary>True while a handle is held.</summary>
    public bool IsOpen => _handle is not null;

    /// <summary>Current length of the open file in bytes.</summary>
    public long Length => RandomAccess.GetLength(Handle);

    /// <summary>Closes the current handle, if any.</summary>
    public void Close()
    {
        _handle?.Dispose();
        _handle = null;
    }

    /// <summary>Closes the current handle and adopts a new one.</summary>
    /// <param name="handle">The replacement handle.</param>
    public void Adopt(SafeFileHandle handle)
    {
        _handle?.Dispose();
        _handle = handle;
    }

    /// <inheritdoc />
    public void Dispose() => Close();
}

/// <summary>Small file helpers shared by the session layer.</summary>
internal static class FileIo
{
    /// <summary>Reads until the destination is full, or fails.</summary>
    /// <param name="handle">Handle to read from.</param>
    /// <param name="offset">Absolute file offset.</param>
    /// <param name="destination">Buffer to fill.</param>
    /// <exception cref="VaultFormatException">The file ended before the buffer was full.</exception>
    public static void ReadExactly(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[done..], offset + done);
            if (read <= 0)
            {
                throw new VaultFormatException(
                    VaultErrorCode.Truncated,
                    $"The vault file ends at offset {offset + done}; {destination.Length - done} more bytes were expected.");
            }

            done += read;
        }
    }

    /// <summary>Reads until the destination is full, or fails.</summary>
    /// <param name="handle">Handle to read from.</param>
    /// <param name="offset">Absolute file offset.</param>
    /// <param name="destination">Buffer to fill.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="VaultFormatException">The file ended before the buffer was full.</exception>
    public static async ValueTask ReadExactlyAsync(SafeFileHandle handle, long offset, Memory<byte> destination, CancellationToken ct)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int read = await RandomAccess.ReadAsync(handle, destination[done..], offset + done, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new VaultFormatException(
                    VaultErrorCode.Truncated,
                    $"The vault file ends at offset {offset + done}; {destination.Length - done} more bytes were expected.");
            }

            done += read;
        }
    }

    /// <summary>Reads the length and last-write time of a path through a fresh handle.</summary>
    /// <param name="path">Path to stat.</param>
    /// <returns>The stat, or <see langword="null"/> when the file does not exist.</returns>
    public static FileStat? TryStat(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists ? new FileStat(info.Length, info.LastWriteTimeUtc) : null;
    }
}

/// <summary>The pair a save compares to detect that the vault changed underneath the session.</summary>
/// <param name="Length">File length in bytes.</param>
/// <param name="LastWriteUtc">Last write time in UTC.</param>
internal readonly record struct FileStat(long Length, DateTime LastWriteUtc);
