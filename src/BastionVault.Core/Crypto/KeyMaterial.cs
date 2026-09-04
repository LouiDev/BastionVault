using System.Security.Cryptography;

namespace BastionVault.Core.Crypto;

/// <summary>
/// A pinned, zero-on-dispose key buffer. Every key in Core lives in one of these
/// (<c>GC.AllocateArray&lt;byte&gt;(n, pinned: true)</c>, zeroed with <c>CryptographicOperations.ZeroMemory</c>).
/// </summary>
/// <remarks>
/// There is deliberately no finalizer: a finalizer would resurrect key bytes onto the finalizer queue and
/// could run while another thread still reads the span. Callers dispose deterministically.
/// </remarks>
public sealed class KeyMaterial : IDisposable
{
    private readonly byte[] _buffer;
    private bool _disposed;

    private KeyMaterial(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _buffer = GC.AllocateArray<byte>(length, pinned: true);
    }

    /// <summary>Allocates a pinned, zero-initialised buffer.</summary>
    /// <param name="length">Buffer length in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static KeyMaterial Allocate(int length) => new(length);

    /// <summary>Allocates a pinned buffer filled from <paramref name="random"/>.</summary>
    /// <param name="length">Buffer length in bytes.</param>
    /// <param name="random">Randomness seam.</param>
    /// <exception cref="ArgumentNullException"><paramref name="random"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public static KeyMaterial Random(int length, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        KeyMaterial material = new(length);
        try
        {
            random.Fill(material._buffer);
        }
        catch
        {
            material.Dispose();
            throw;
        }

        return material;
    }

    /// <summary>Allocates a pinned buffer holding a copy of <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Bytes to copy in.</param>
    public static KeyMaterial From(ReadOnlySpan<byte> bytes)
    {
        KeyMaterial material = new(bytes.Length);
        bytes.CopyTo(material._buffer);
        return material;
    }

    /// <summary>The key bytes. Throws once the instance is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer;
        }
    }

    /// <summary>Length of the buffer in bytes.</summary>
    public int Length => _buffer.Length;

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Zeroes and releases the buffer. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_buffer);
    }

    /// <summary>
    /// The backing buffer regardless of disposal state. Only the tests use it, to prove that
    /// <see cref="Dispose"/> really zeroed the bytes.
    /// </summary>
    internal ReadOnlySpan<byte> BufferUnchecked => _buffer;
}
