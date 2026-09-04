using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace Bastion.Core.Session;

/// <summary>
/// Ciphertext of one imported or re-encrypted file that is not in the vault file yet. A slot starts in
/// memory and is moved into the staging container when the aggregate in-memory budget is exceeded.
/// </summary>
internal sealed class StagedBlobSource : IBlobSource
{
    private readonly StagingStore _store;
    private ArrayBufferWriter<byte>? _incoming;
    private MemoryBlobSource? _memory;
    private long _containerOffset = -1;

    /// <summary>Creates an empty slot.</summary>
    /// <param name="store">The store that owns the slot.</param>
    internal StagedBlobSource(StagingStore store)
    {
        _store = store;
        _incoming = new ArrayBufferWriter<byte>();
    }

    /// <inheritdoc />
    public long Length { get; private set; }

    /// <summary>True while ciphertext is still being appended.</summary>
    internal bool IsOpen => _incoming is not null;

    /// <summary>True when the ciphertext of this slot lives in memory.</summary>
    internal bool IsInMemory => _containerOffset < 0;

    /// <summary>End offset of the slot inside the container, or 0 while it is in memory.</summary>
    internal long ContainerEnd => _containerOffset < 0 ? 0 : _containerOffset + Length;

    /// <inheritdoc />
    public void Read(long offset, Span<byte> destination)
    {
        StoredBlobSource.RequireRange(offset, destination.Length, Length);
        if (_memory is not null)
        {
            _memory.Read(offset, destination);
            return;
        }

        if (_containerOffset >= 0)
        {
            _store.ReadContainer(_containerOffset + offset, destination);
            return;
        }

        throw new VaultIoException(VaultErrorCode.IoError, "The staged content of this entry is no longer available.");
    }

    /// <summary>Appends ciphertext while the slot is being written.</summary>
    /// <param name="ciphertext">Bytes to append.</param>
    internal void AppendToMemory(ReadOnlySpan<byte> ciphertext)
    {
        _incoming!.Write(ciphertext);
        Length += ciphertext.Length;
    }

    /// <summary>Notes that ciphertext was appended straight to the container.</summary>
    /// <param name="offset">Offset the first byte of the slot went to.</param>
    /// <param name="count">Number of bytes appended.</param>
    internal void AppendToContainer(long offset, int count)
    {
        if (_containerOffset < 0)
        {
            _containerOffset = offset;
            _incoming = null;
        }

        Length += count;
    }

    /// <summary>The buffered bytes, while the slot is still in memory.</summary>
    internal ReadOnlySpan<byte> BufferedBytes =>
        _incoming is not null ? _incoming.WrittenSpan : (_memory is not null ? _memory.Bytes : default);

    /// <summary>Closes the slot: the ciphertext is complete.</summary>
    internal void Complete()
    {
        if (_incoming is null)
        {
            return;
        }

        _memory = new MemoryBlobSource(_incoming.WrittenSpan.ToArray());
        _incoming = null;
    }

    /// <summary>Moves the slot into the container, releasing its managed buffer.</summary>
    /// <param name="offset">Offset the bytes were written to.</param>
    internal void MoveToContainer(long offset)
    {
        _containerOffset = offset;
        _memory = null;
        _incoming = null;
    }

    /// <summary>Drops every reference to the ciphertext of this slot.</summary>
    internal void Forget()
    {
        _memory = null;
        _incoming = null;
        _containerOffset = -1;
        Length = 0;
    }
}

/// <summary>
/// Holds the ciphertext of pending imports and in-vault copies (FORMAT.md section 8.5): in memory up to
/// <see cref="OpenOptions.InMemoryStagingLimit"/> in aggregate, then in one append-only, delete-on-close
/// container next to the vault (or in the fallback directory when that is not a good place).
/// </summary>
internal sealed class StagingStore : IDisposable
{
    /// <summary>Head room a save wants on the vault volume beyond the new file itself (FORMAT.md section 8.3).</summary>
    public const long SaveHeadroomBytes = 64L * 1024 * 1024;

    private readonly string _vaultPath;
    private readonly IVaultPaths _paths;
    private readonly OpenOptions _options;
    private readonly Guid _sessionId;
    private readonly List<StagedBlobSource> _slots = [];

    private SafeFileHandle? _container;
    private string? _containerPath;
    private long _containerLength;
    private long _memoryBytes;
    private StagedBlobSource? _open;

    /// <summary>Creates a store for one session.</summary>
    /// <param name="vaultPath">Absolute path of the vault being edited.</param>
    /// <param name="paths">Naming and placement seam.</param>
    /// <param name="options">Open options carrying the in-memory budget and the staging override.</param>
    /// <param name="sessionId">Id that makes the container name unique.</param>
    public StagingStore(string vaultPath, IVaultPaths paths, OpenOptions options, Guid sessionId)
    {
        _vaultPath = vaultPath;
        _paths = paths;
        _options = options;
        _sessionId = sessionId;
    }

    /// <summary>Total staged ciphertext held by this store.</summary>
    public long StagedBytes { get; private set; }

    /// <summary>Path of the container file, or <see langword="null"/> while everything fits in memory.</summary>
    public string? ContainerPath => _containerPath;

    /// <summary>Opens a slot; the caller appends the ciphertext chunk by chunk.</summary>
    public StagedBlobSource BeginBlob()
    {
        if (_open is not null)
        {
            throw new InvalidOperationException("Another staged blob is still open.");
        }

        var slot = new StagedBlobSource(this);
        _slots.Add(slot);
        _open = slot;
        return slot;
    }

    /// <summary>Appends ciphertext to the open slot.</summary>
    /// <param name="slot">The open slot.</param>
    /// <param name="ciphertext">Bytes to append.</param>
    public void Append(StagedBlobSource slot, ReadOnlySpan<byte> ciphertext)
    {
        if (!ReferenceEquals(slot, _open))
        {
            throw new InvalidOperationException("Only the slot that is currently open accepts writes.");
        }

        if (_container is not null)
        {
            long offset = _containerLength;
            WriteContainer(ciphertext);
            slot.AppendToContainer(offset, ciphertext.Length);
            StagedBytes += ciphertext.Length;
            return;
        }

        slot.AppendToMemory(ciphertext);
        _memoryBytes += ciphertext.Length;
        StagedBytes += ciphertext.Length;

        if (_memoryBytes > _options.InMemoryStagingLimit)
        {
            SpillToContainer();
        }
    }

    /// <summary>Closes the open slot.</summary>
    /// <param name="slot">The slot to close.</param>
    public void EndBlob(StagedBlobSource slot)
    {
        slot.Complete();
        _open = null;
    }

    /// <summary>
    /// Drops the ciphertext of the given slots (a cancelled or failed import) and truncates the container
    /// back to the highest offset that is still referenced.
    /// </summary>
    /// <param name="slots">Slots to discard.</param>
    public void Discard(IEnumerable<StagedBlobSource> slots)
    {
        foreach (StagedBlobSource slot in slots)
        {
            if (!_slots.Remove(slot))
            {
                continue;
            }

            if (slot.IsInMemory)
            {
                _memoryBytes -= slot.Length;
            }

            StagedBytes -= slot.Length;
            slot.Forget();
            if (ReferenceEquals(slot, _open))
            {
                _open = null;
            }
        }

        TruncateContainerToLiveTail();
    }

    /// <summary>Drops everything: the memory buffers and the container file.</summary>
    public void Clear()
    {
        foreach (StagedBlobSource slot in _slots)
        {
            slot.Forget();
        }

        _slots.Clear();
        _open = null;
        _memoryBytes = 0;
        StagedBytes = 0;
        CloseContainer();
    }

    /// <summary>
    /// Pre-flight for an import (FORMAT.md section 8.5): the staged bytes must fit where staging lives and
    /// the resulting vault must fit on the vault volume.
    /// </summary>
    /// <param name="additionalStagedBytes">Ciphertext the import will stage.</param>
    /// <param name="estimatedVaultLength">Length the vault file will have after the next save.</param>
    /// <exception cref="VaultResourceException"><see cref="VaultErrorCode.DiskFull"/> when a volume is too small.</exception>
    public void PreflightSpace(long additionalStagedBytes, long estimatedVaultLength)
    {
        RequireSpace(System.IO.Path.GetDirectoryName(_vaultPath), estimatedVaultLength + SaveHeadroomBytes);

        if (WouldStageToDisk(additionalStagedBytes))
        {
            RequireSpace(ResolveStagingDirectory(), additionalStagedBytes);
        }
    }

    /// <summary>
    /// True when the next <paramref name="additionalStagedBytes"/> bytes will reach the staging
    /// container. Once the store has spilled, every further byte goes to disk however small it is, even
    /// though the in-memory counter is back at zero, so the container state has to be part of the test.
    /// </summary>
    /// <param name="additionalStagedBytes">Ciphertext about to be staged.</param>
    public bool WouldStageToDisk(long additionalStagedBytes) =>
        _container is not null || additionalStagedBytes > _options.InMemoryStagingLimit - _memoryBytes;

    /// <summary>Reads bytes from the container.</summary>
    /// <param name="offset">Absolute offset in the container.</param>
    /// <param name="destination">Buffer to fill.</param>
    internal void ReadContainer(long offset, Span<byte> destination)
    {
        SafeFileHandle handle = _container
            ?? throw new VaultIoException(VaultErrorCode.IoError, "The staging container is not open.");
        try
        {
            FileIo.ReadExactly(handle, offset, destination);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, _containerPath);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Clear();

    /// <summary>Appends bytes to the container.</summary>
    /// <param name="bytes">Bytes to write.</param>
    private void WriteContainer(ReadOnlySpan<byte> bytes)
    {
        try
        {
            RandomAccess.Write(_container!, bytes, _containerLength);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, _containerPath);
        }

        _containerLength += bytes.Length;
    }

    /// <summary>Moves every in-memory slot into the container and switches the store to container mode.</summary>
    private void SpillToContainer()
    {
        OpenContainer();

        foreach (StagedBlobSource slot in _slots)
        {
            if (!slot.IsInMemory || slot.Length == 0)
            {
                continue;
            }

            long offset = _containerLength;
            WriteContainer(slot.BufferedBytes);
            slot.MoveToContainer(offset);
        }

        _memoryBytes = 0;
    }

    /// <summary>Creates the container file and takes an exclusive, delete-on-close handle on it.</summary>
    private void OpenContainer()
    {
        if (_container is not null)
        {
            return;
        }

        string directory = ResolveStagingDirectory();
        string name = System.IO.Path.GetFileName(_paths.StagingContainerFor(_vaultPath, _sessionId));
        string path = System.IO.Path.Combine(directory, name);

        try
        {
            using (File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // The file must exist before its attributes can be set: an exclusive handle blocks the
                // open that File.SetAttributes performs internally.
            }

            TrySetTemporary(path);
            _container = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            _containerPath = path;
            _containerLength = 0;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            throw IoGuard.Translate(ex, path);
        }
    }

    /// <summary>Closes and thereby deletes the container.</summary>
    private void CloseContainer()
    {
        _container?.Dispose();
        _container = null;
        _containerLength = 0;

        if (_containerPath is not null)
        {
            TryDelete(_containerPath);
            _containerPath = null;
        }
    }

    /// <summary>Shrinks the container to the end of the last slot that is still referenced.</summary>
    private void TruncateContainerToLiveTail()
    {
        if (_container is null)
        {
            return;
        }

        long live = 0;
        foreach (StagedBlobSource slot in _slots)
        {
            live = Math.Max(live, slot.ContainerEnd);
        }

        if (live >= _containerLength)
        {
            return;
        }

        try
        {
            RandomAccess.SetLength(_container, live);
            _containerLength = live;
        }
        catch (IOException)
        {
            // Reclaiming space is best effort; the container disappears when the session ends.
        }
    }

    /// <summary>
    /// Decides where the container lives: the user override, else the vault directory, else the fallback
    /// directory when the vault directory is unusable or sits under a cloud-sync root.
    /// </summary>
    private string ResolveStagingDirectory()
    {
        if (!string.IsNullOrEmpty(_options.StagingDirectoryOverride))
        {
            return EnsureDirectory(_options.StagingDirectoryOverride);
        }

        string? vaultDirectory = System.IO.Path.GetDirectoryName(_vaultPath);
        if (!string.IsNullOrEmpty(vaultDirectory) &&
            !_paths.IsUnderCloudSyncRoot(_vaultPath) &&
            IsWritable(vaultDirectory))
        {
            return vaultDirectory;
        }

        return EnsureDirectory(_paths.FallbackStagingDirectory);
    }

    /// <summary>Creates a directory if needed and returns it.</summary>
    /// <param name="directory">Directory to ensure.</param>
    private static string EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, directory);
        }
    }

    /// <summary>True when a probe file can be created in the directory.</summary>
    /// <param name="directory">Directory to test.</param>
    internal static bool IsWritable(string directory)
    {
        string probe = System.IO.Path.Combine(directory, $".bastion-probe-{Guid.NewGuid():N}");
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Throws when a volume cannot hold the requested number of bytes.</summary>
    /// <param name="directory">Directory on the volume to test.</param>
    /// <param name="requiredBytes">Bytes that must fit.</param>
    private static void RequireSpace(string? directory, long requiredBytes)
    {
        if (string.IsNullOrEmpty(directory) || requiredBytes <= 0)
        {
            return;
        }

        long? free = AvailableFreeSpace(directory);
        if (free is null || free >= requiredBytes)
        {
            return;
        }

        throw new VaultResourceException(
            VaultErrorCode.DiskFull,
            $"The volume holding {directory} has {free.Value} bytes free; {requiredBytes} are required.")
        {
            RequiredBytes = requiredBytes,
            AvailableBytes = free.Value,
        };
    }

    /// <summary>Free bytes on the volume of a directory, or <see langword="null"/> when it cannot be determined.</summary>
    /// <param name="directory">Directory to test.</param>
    internal static long? AvailableFreeSpace(string directory)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Marks a file as temporary; failure is not fatal.</summary>
    /// <param name="path">File to mark.</param>
    private static void TrySetTemporary(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The attribute is a hint to the file system cache, not a correctness requirement.
        }
    }

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The delete-on-close handle removes the file; this is only a belt-and-braces sweep.
        }
    }
}
