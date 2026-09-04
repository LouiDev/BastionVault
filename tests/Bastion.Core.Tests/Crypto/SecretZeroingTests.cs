using Bastion.Core.Crypto;

namespace Bastion.Core.Tests.Crypto;

/// <summary>
/// Disposing a secret must leave the pinned buffer all zero (API.md rule 4). The tests reach the
/// backing arrays through the internal test hooks, because the public accessors refuse to hand out a
/// disposed buffer.
/// </summary>
public sealed class SecretZeroingTests
{
    [Fact]
    public void KeyMaterial_Dispose_ZeroesTheBuffer()
    {
        KeyMaterial material = KeyMaterial.Random(64, SystemRandomSource.Instance);
        Assert.Contains(material.Span.ToArray(), b => b != 0);

        material.Dispose();

        Assert.True(material.IsDisposed);
        Assert.All(material.BufferUnchecked.ToArray(), b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void KeyMaterial_Span_ThrowsAfterDispose()
    {
        KeyMaterial material = KeyMaterial.Allocate(32);
        material.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = material.Span.Length; });
    }

    [Fact]
    public void KeyMaterial_Dispose_IsIdempotent()
    {
        KeyMaterial material = KeyMaterial.From([1, 2, 3, 4]);
        material.Dispose();
        material.Dispose();

        Assert.True(material.IsDisposed);
        Assert.Equal(4, material.Length);
    }

    [Fact]
    public void KeyMaterial_From_CopiesTheInput()
    {
        byte[] source = [9, 8, 7];
        using KeyMaterial material = KeyMaterial.From(source);
        source[0] = 0;

        Assert.Equal(new byte[] { 9, 8, 7 }, material.Span.ToArray());
    }

    [Fact]
    public void Passphrase_Dispose_ZeroesTheBuffer()
    {
        Passphrase passphrase = Passphrase.FromString("correct horse battery staple");
        Assert.Contains(passphrase.Bytes.ToArray(), b => b != 0);

        passphrase.Dispose();

        Assert.All(passphrase.BufferUnchecked.ToArray(), b => Assert.Equal((byte)0, b));
        Assert.Throws<ObjectDisposedException>(() => { _ = passphrase.Bytes.Length; });
    }

    [Fact]
    public void Passphrase_Clone_IsIndependentOfTheOriginal()
    {
        using Passphrase original = Passphrase.FromString("s3cret");
        Passphrase copy = original.Clone();

        Assert.Equal(original.Bytes.ToArray(), copy.Bytes.ToArray());

        copy.Dispose();

        Assert.All(copy.BufferUnchecked.ToArray(), b => Assert.Equal((byte)0, b));
        Assert.Equal("s3cret"u8.ToArray(), original.Bytes.ToArray());
    }

    [Fact]
    public void KeyFile_Dispose_ZeroesTheDigest()
    {
        KeyFile keyFile = KeyFile.FromBytes([1, 2, 3, 4, 5]);
        Assert.Contains(keyFile.Digest.ToArray(), b => b != 0);

        keyFile.Dispose();

        Assert.All(keyFile.BufferUnchecked.ToArray(), b => Assert.Equal((byte)0, b));
        Assert.Throws<ObjectDisposedException>(() => { _ = keyFile.Digest.Length; });
    }
}
