using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Controls;
using Bastion.Core;

namespace Bastion.App.Services;

/// <summary>
/// Turns a <see cref="PasswordBox"/>'s <see cref="SecureString"/> into a <see cref="Passphrase"/>
/// without ever creating a managed <see cref="string"/> (UI-CONTRACT.md section 1.3).
/// The intermediate character buffer is pinned and zeroed before this method returns; the
/// unmanaged BSTR the CLR hands out is zeroed and freed in the same <c>finally</c>.
/// </summary>
public static class PasswordBoxBinder
{
    /// <summary>
    /// Builds a passphrase from the characters of a secure string. The seam exists so tests can
    /// observe the buffer handling, and so a host that has no real Core (the demo) can substitute
    /// its own factory; production code never sets it.
    /// </summary>
    /// <param name="buffer">Pinned buffer holding the characters; it is zeroed after the call returns.</param>
    /// <param name="length">Number of valid characters in <paramref name="buffer"/>.</param>
    /// <returns>
    /// The passphrase, or <see langword="null"/> for a host that holds no key material: the
    /// <c>--demo</c> host runs the whole UI against a fake session and never derives a key.
    /// </returns>
    internal delegate Passphrase? PassphraseFactory(char[] buffer, int length);

    /// <summary>The factory used to build a passphrase. Defaults to <see cref="Passphrase.FromChars"/>.</summary>
    internal static PassphraseFactory Factory { get; set; } =
        static (buffer, length) => Passphrase.FromChars(buffer.AsSpan(0, length));

    /// <summary>Reads a password box and returns a passphrase over its characters.</summary>
    /// <param name="box">The password box to read.</param>
    public static Passphrase? ToPassphrase(PasswordBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        using SecureString secure = box.SecurePassword;
        return ToPassphrase(secure);
    }

    /// <summary>Returns a passphrase over the characters of <paramref name="secure"/>.</summary>
    /// <param name="secure">The secure string to convert; it is not disposed.</param>
    public static Passphrase? ToPassphrase(SecureString secure)
    {
        ArgumentNullException.ThrowIfNull(secure);

        int length = secure.Length;
        char[] buffer = GC.AllocateArray<char>(Math.Max(length, 1), pinned: true);
        IntPtr bstr = IntPtr.Zero;

        try
        {
            bstr = Marshal.SecureStringToCoTaskMemUnicode(secure);
            unsafe
            {
                var source = new ReadOnlySpan<char>((void*)bstr, length);
                source.CopyTo(buffer);
            }

            return Factory(buffer, length);
        }
        finally
        {
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(bstr);
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    /// <summary>
    /// Estimates the strength of the characters in a password box without ever letting them
    /// become a managed string.
    /// </summary>
    /// <param name="box">The password box to measure.</param>
    public static PasswordStrengthResult EstimateStrength(PasswordBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        using SecureString secure = box.SecurePassword;
        return Read(secure, static chars => PasswordStrength.Estimate(chars));
    }

    /// <summary>True when two password boxes hold exactly the same characters.</summary>
    /// <param name="first">First box.</param>
    /// <param name="second">Second box, usually the confirmation field.</param>
    public static bool Matches(PasswordBox first, PasswordBox second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        using SecureString a = first.SecurePassword;
        using SecureString b = second.SecurePassword;
        if (a.Length != b.Length)
        {
            return false;
        }

        IntPtr left = IntPtr.Zero;
        IntPtr right = IntPtr.Zero;
        try
        {
            left = Marshal.SecureStringToCoTaskMemUnicode(a);
            right = Marshal.SecureStringToCoTaskMemUnicode(b);
            unsafe
            {
                var spanA = new ReadOnlySpan<char>((void*)left, a.Length);
                var spanB = new ReadOnlySpan<char>((void*)right, b.Length);
                return spanA.SequenceEqual(spanB);
            }
        }
        finally
        {
            if (left != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(left);
            }

            if (right != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(right);
            }
        }
    }

    /// <summary>
    /// Copies the characters of a secure string into a pinned buffer, hands the span to
    /// <paramref name="reader"/>, and zeroes both the buffer and the unmanaged BSTR afterwards.
    /// </summary>
    /// <typeparam name="TResult">What the reader produces.</typeparam>
    /// <param name="secure">The secure string to read.</param>
    /// <param name="reader">Receives the characters; it must not let them escape.</param>
    internal static TResult Read<TResult>(SecureString secure, SpanReader<TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(secure);
        ArgumentNullException.ThrowIfNull(reader);

        int length = secure.Length;
        char[] buffer = GC.AllocateArray<char>(Math.Max(length, 1), pinned: true);
        IntPtr bstr = IntPtr.Zero;

        try
        {
            bstr = Marshal.SecureStringToCoTaskMemUnicode(secure);
            unsafe
            {
                new ReadOnlySpan<char>((void*)bstr, length).CopyTo(buffer);
            }

            return reader(buffer.AsSpan(0, length));
        }
        finally
        {
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(bstr);
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    /// <summary>A callback that reads password characters without letting them escape.</summary>
    /// <typeparam name="TResult">What the callback produces.</typeparam>
    /// <param name="characters">The password characters.</param>
    internal delegate TResult SpanReader<out TResult>(ReadOnlySpan<char> characters);

    /// <summary>True when the box holds at least one character.</summary>
    /// <param name="box">The password box to inspect.</param>
    public static bool HasContent(PasswordBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        using SecureString secure = box.SecurePassword;
        return secure.Length > 0;
    }
}
