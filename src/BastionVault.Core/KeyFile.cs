using System.Security.Cryptography;
using BastionVault.Core.Crypto;
using BastionVault.Core.Format;

namespace BastionVault.Core;

/// <summary>
/// An optional second factor. Only its HMAC-SHA256 digest is kept in memory (FORMAT.md §2.2);
/// disposing zeroes it. The header carries no indication that a keyfile is required.
/// </summary>
public sealed class KeyFile : IDisposable
{
    private readonly byte[] _digest;
    private bool _disposed;

    private KeyFile(byte[] digest, string? sourcePath)
    {
        _digest = digest;
        SourcePath = sourcePath;
    }

    /// <summary>Reads a keyfile from disk.</summary>
    /// <param name="path">Path of the keyfile. Its length must be 1 byte .. 1 MiB.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.UnsupportedParameters"/> — the file is empty or larger than 1 MiB.</exception>
    /// <exception cref="VaultIoException"><see cref="VaultErrorCode.IoError"/> — the file could not be read.</exception>
    public static KeyFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] content;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            RequireLength(length);

            content = new byte[(int)length];
            stream.ReadExactly(content);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new VaultIoException(VaultErrorCode.IoError, $"The keyfile could not be read: {ex.Message}", ex)
            {
                OffendingPath = path,
            };
        }

        try
        {
            return new KeyFile(VaultKeys.ComputeKeyfileDigest(content), fullPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>Creates a keyfile from bytes already in memory (1 byte .. 1 MiB).</summary>
    /// <param name="content">The keyfile content.</param>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.UnsupportedParameters"/> — the content is empty or larger than 1 MiB.</exception>
    public static KeyFile FromBytes(ReadOnlySpan<byte> content)
    {
        RequireLength(content.Length);
        return new KeyFile(VaultKeys.ComputeKeyfileDigest(content), null);
    }

    /// <summary>Generates fresh random keyfile content for the user to save.</summary>
    /// <param name="length">Number of random bytes; 64 by default.</param>
    /// <param name="random">Randomness seam; the OS CSPRNG when <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is outside 1 byte .. 1 MiB.</exception>
    public static byte[] GenerateContent(int length = 64, IRandomSource? random = null)
    {
        if (length is < VaultLimits.MinKeyFileBytes or > VaultLimits.MaxKeyFileBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"A keyfile must be {VaultLimits.MinKeyFileBytes} .. {VaultLimits.MaxKeyFileBytes} bytes.");
        }

        byte[] content = new byte[length];
        (random ?? SystemRandomSource.Instance).Fill(content);
        return content;
    }

    /// <summary>Path the keyfile was loaded from, or <see langword="null"/> when it came from memory.</summary>
    public string? SourcePath { get; }

    /// <summary>The 32-byte digest <c>HMAC-SHA256("bastion/v1/keyfile", keyfileBytes)</c> (FORMAT.md §2.2).</summary>
    /// <exception cref="ObjectDisposedException">The keyfile has been disposed.</exception>
    public ReadOnlySpan<byte> Digest
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _digest;
        }
    }

    /// <summary>Zeroes and releases the digest.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_digest);
    }

    /// <summary>
    /// The backing digest buffer regardless of disposal state. Only the tests use it, to prove that
    /// <see cref="Dispose"/> really zeroed the bytes.
    /// </summary>
    internal ReadOnlySpan<byte> BufferUnchecked => _digest;

    private static void RequireLength(long length)
    {
        if (length is < VaultLimits.MinKeyFileBytes or > VaultLimits.MaxKeyFileBytes)
        {
            throw new VaultFormatException(
                VaultErrorCode.UnsupportedParameters,
                $"A keyfile must be {VaultLimits.MinKeyFileBytes} byte .. {VaultLimits.MaxKeyFileBytes} bytes (was {length}).");
        }
    }
}
