using System.Security.Cryptography;
using System.Text;
using Bastion.Core.Format;

namespace Bastion.Core;

/// <summary>
/// A password held as pinned, zeroable bytes. Secrets are never a <see cref="string"/> in this API;
/// disposing zeroes the buffer (FORMAT.md §2.1).
/// </summary>
public sealed class Passphrase : IDisposable
{
    private readonly byte[] _bytes;
    private bool _disposed;

    private Passphrase(byte[] bytes) => _bytes = bytes;

    /// <summary>
    /// Creates a passphrase from a string: NFC-normalised, UTF-8 encoded, validated to 1 .. 1024 bytes.
    /// </summary>
    /// <param name="password">The password text. Unpaired surrogates are rejected before normalisation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="password"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The text is empty, contains unpaired surrogates, or exceeds 1024 UTF-8 bytes.</exception>
    public static Passphrase FromString(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        RequireWellFormed(password, nameof(password));
        return FromNormalizedText(Normalize(password), nameof(password));
    }

    /// <summary>
    /// Same as <see cref="FromString(string)"/> but without materialising a <see cref="string"/> when the input is ASCII.
    /// </summary>
    /// <param name="password">The password characters.</param>
    /// <exception cref="ArgumentException">The text is empty, contains unpaired surrogates, or exceeds 1024 UTF-8 bytes.</exception>
    public static Passphrase FromChars(ReadOnlySpan<char> password)
    {
        // ASCII is already in NFC and encodes one byte per character, so the whole normalisation and
        // encoding round trip (which would need a managed string that cannot be zeroed) is skipped.
        if (Ascii.IsValid(password))
        {
            RequireLength(password.Length, nameof(password));
            byte[] buffer = GC.AllocateArray<byte>(password.Length, pinned: true);
            Ascii.FromUtf16(password, buffer, out _);
            return new Passphrase(buffer);
        }

        RequireWellFormed(password, nameof(password));
        return FromNormalizedText(Normalize(new string(password)), nameof(password));
    }

    /// <summary>The UTF-8 password bytes (no NUL terminator, no trimming, no case folding).</summary>
    /// <exception cref="ObjectDisposedException">The passphrase has been disposed.</exception>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    /// <summary>Length of <see cref="Bytes"/> in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>Returns an independent copy that the caller owns and must dispose.</summary>
    /// <exception cref="ObjectDisposedException">The passphrase has been disposed.</exception>
    public Passphrase Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] copy = GC.AllocateArray<byte>(_bytes.Length, pinned: true);
        _bytes.CopyTo(copy.AsSpan());
        return new Passphrase(copy);
    }

    /// <summary>Zeroes and releases the password bytes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_bytes);
    }

    /// <summary>
    /// The backing buffer regardless of disposal state. Only the tests use it, to prove that
    /// <see cref="Dispose"/> really zeroed the bytes.
    /// </summary>
    internal ReadOnlySpan<byte> BufferUnchecked => _bytes;

    private static Passphrase FromNormalizedText(string normalized, string parameterName)
    {
        int byteCount = Encoding.UTF8.GetByteCount(normalized);
        RequireLength(byteCount, parameterName);

        byte[] buffer = GC.AllocateArray<byte>(byteCount, pinned: true);
        Encoding.UTF8.GetBytes(normalized, buffer);
        return new Passphrase(buffer);
    }

    private static string Normalize(string text) =>
        text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

    private static void RequireLength(int byteCount, string parameterName)
    {
        if (byteCount < VaultLimits.MinPasswordBytes)
        {
            throw new ArgumentException("The password must not be empty.", parameterName);
        }

        if (byteCount > VaultLimits.MaxPasswordBytes)
        {
            throw new ArgumentException(
                $"The password must be at most {VaultLimits.MaxPasswordBytes} UTF-8 bytes (was {byteCount}).",
                parameterName);
        }
    }

    /// <summary>Rejects unpaired surrogates before normalisation, per FORMAT.md §2.1.</summary>
    private static void RequireWellFormed(ReadOnlySpan<char> text, string parameterName)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsSurrogate(c))
            {
                continue;
            }

            if (!char.IsHighSurrogate(c) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
            {
                throw new ArgumentException("The password contains an unpaired surrogate.", parameterName);
            }

            i++;
        }
    }
}
