using System.Security;
using BastionVault.App.Services;

namespace BastionVault.App.Tests;

/// <summary>
/// The password path. The rule is absolute: characters go from the box into a pinned buffer and
/// into a <c>Passphrase</c>, and everything in between is zeroed. These tests watch the buffer.
/// </summary>
public sealed class PasswordBoxBinderTests
{
    [Fact]
    public void CharactersReachTheReaderUnchanged()
    {
        using SecureString secure = Secure("correct horse battery staple");

        string seen = PasswordBoxBinder.Read(secure, static chars => new string(chars));

        Assert.Equal("correct horse battery staple", seen);
    }

    [Fact]
    public void NonAsciiSurvives()
    {
        using SecureString secure = Secure("Schlüssel-Übung-中文");

        string seen = PasswordBoxBinder.Read(secure, static chars => new string(chars));

        Assert.Equal("Schlüssel-Übung-中文", seen);
    }

    [Fact]
    public void AnEmptySecureStringIsHandled()
    {
        using SecureString secure = Secure(string.Empty);

        string seen = PasswordBoxBinder.Read(secure, static chars => new string(chars));

        Assert.Equal(string.Empty, seen);
    }

    [Fact]
    public void TheScratchBufferIsZeroedBeforeToPassphraseReturns()
    {
        char[]? captured = null;
        int capturedLength = 0;

        PasswordBoxBinder.PassphraseFactory original = PasswordBoxBinder.Factory;
        try
        {
            PasswordBoxBinder.Factory = (buffer, length) =>
            {
                captured = buffer;
                capturedLength = length;

                // While the factory runs, the characters are still there: that is the whole point
                // of the seam, and it is what a real Passphrase.FromChars would read.
                Assert.Equal("hunter2!", new string(buffer, 0, length));
                return null;
            };

            using SecureString secure = Secure("hunter2!");
            Assert.Null(PasswordBoxBinder.ToPassphrase(secure));
        }
        finally
        {
            PasswordBoxBinder.Factory = original;
        }

        Assert.NotNull(captured);
        Assert.Equal(8, capturedLength);
        Assert.All(captured!, c => Assert.Equal('\0', c));
    }

    [Fact]
    public void TheBufferIsZeroedEvenWhenTheFactoryThrows()
    {
        char[]? captured = null;

        PasswordBoxBinder.PassphraseFactory original = PasswordBoxBinder.Factory;
        try
        {
            PasswordBoxBinder.Factory = (buffer, _) =>
            {
                captured = buffer;
                throw new InvalidOperationException("boom");
            };

            using SecureString secure = Secure("hunter2!");
            Assert.Throws<InvalidOperationException>(() => PasswordBoxBinder.ToPassphrase(secure));
        }
        finally
        {
            PasswordBoxBinder.Factory = original;
        }

        Assert.NotNull(captured);
        Assert.All(captured!, c => Assert.Equal('\0', c));
    }

    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (char c in text)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }
}
