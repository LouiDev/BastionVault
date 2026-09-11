using System.Buffers;

namespace BastionVault.Core.Session;

/// <summary>
/// A read-only, seekable stream over the plaintext of one blob. Every chunk is authenticated before a
/// single byte of it is handed out; a tag failure surfaces as <see cref="VaultErrorCode.DataCorrupt"/>.
/// Seeking is cheap because every chunk carries its own tag bound to its position (FORMAT.md section
/// 5): landing on an offset decrypts and authenticates just the chunk that holds it, so a container
/// parser that jumps to an index at the end of the file does not pay for the bytes in between.
/// </summary>
internal sealed class DecryptingBlobStream : Stream
{
    private readonly BlobReader _reader;
    private byte[]? _cipherBuffer;
    private byte[]? _plainBuffer;
    private uint _nextChunk;
    private int _available;
    private int _consumed;
    private long _position;
    private bool _disposed;

    /// <summary>Opens a plaintext stream over a blob and takes ownership of the reader.</summary>
    /// <param name="reader">Reader over the blob; disposed with the stream.</param>
    public DecryptingBlobStream(BlobReader reader)
    {
        _reader = reader;

        // Sized from the blob, never from the declared chunk size: see BlobReader.MaxChunkPlaintextLength.
        _cipherBuffer = ArrayPool<byte>.Shared.Rent(reader.MaxChunkCiphertextLength);
        _plainBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(reader.MaxChunkPlaintextLength, 1));
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _reader.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_consumed == _available && !FillNextChunk())
        {
            return 0;
        }

        int take = Math.Min(buffer.Length, _available - _consumed);
        _plainBuffer!.AsSpan(_consumed, take).CopyTo(buffer);
        _consumed += take;
        _position += take;
        return take;
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception ex)
        {
            return ValueTask.FromException<int>(ex);
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// A target inside the chunk that is already decrypted only moves the cursor. Any other target
    /// inside the blob decrypts and authenticates its chunk right away, so a bad tag surfaces from the
    /// seek rather than from the read that follows. A target at or beyond the end is allowed, as for
    /// any <see cref="Stream"/>; reads there return zero.
    /// </remarks>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown seek origin."),
        };

        if (target < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A seek before the start of the stream is not possible.");
        }

        if (target == _position)
        {
            return target;
        }

        long loadedStart = _position - _consumed;
        if (_available > 0 && target >= loadedStart && target < loadedStart + _available)
        {
            _consumed = (int)(target - loadedStart);
        }
        else if (target >= Length)
        {
            _nextChunk = _reader.ChunkCount;
            _available = 0;
            _consumed = 0;
        }
        else
        {
            _nextChunk = (uint)(target / _reader.ChunkSize);
            _available = 0;
            _consumed = 0;
            if (FillNextChunk())
            {
                long chunkStart = (long)(_nextChunk - 1) * _reader.ChunkSize;
                _consumed = (int)Math.Min(target - chunkStart, _available);
            }
        }

        _position = target;
        return target;
    }

    /// <inheritdoc />
    public override void SetLength(long value) =>
        throw new NotSupportedException("A vault content stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A vault content stream is read-only.");

    /// <summary>Reads and authenticates the next chunk.</summary>
    /// <returns>False at the end of the blob.</returns>
    private bool FillNextChunk()
    {
        while (_nextChunk < _reader.ChunkCount)
        {
            uint index = _nextChunk++;
            int plainLength = _reader.ReadPlaintextChunk(index, _cipherBuffer!, _plainBuffer!);
            _available = plainLength;
            _consumed = 0;
            if (plainLength > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _reader.Dispose();
                if (_cipherBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_cipherBuffer);
                    _cipherBuffer = null;
                }

                if (_plainBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_plainBuffer, clearArray: true);
                    _plainBuffer = null;
                }
            }
        }

        base.Dispose(disposing);
    }
}
