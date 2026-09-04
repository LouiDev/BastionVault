using System.Security.Cryptography;
using Bastion.Core.Crypto;
using Bastion.Core.Format;
using IoPath = System.IO.Path;

namespace Bastion.Core.Tests.Vault;

/// <summary>
/// A vault file taken apart the way an attacker who knows the password can take it apart: header
/// parsed, vault key unwrapped, index decrypted, every byte range addressable. The adversarial tests
/// of FORMAT.md section 10 build their damaged files through this class so that each one violates
/// exactly one rule.
/// </summary>
internal sealed class VaultImage : IDisposable
{
    private readonly KeyMaterial _vaultKey;
    private bool _disposed;

    /// <summary>Takes ownership of the unwrapped vault key.</summary>
    /// <param name="sourcePath">Path the bytes were read from.</param>
    /// <param name="bytes">A private, mutable copy of the whole file.</param>
    /// <param name="header">The parsed header.</param>
    /// <param name="index">The decrypted primary index.</param>
    /// <param name="vaultKey">The unwrapped 32-byte vault key.</param>
    private VaultImage(string sourcePath, byte[] bytes, VaultHeader header, VaultIndex index, KeyMaterial vaultKey)
    {
        SourcePath = sourcePath;
        Bytes = bytes;
        Header = header;
        Index = index;
        _vaultKey = vaultKey;
    }

    /// <summary>Path the image was read from.</summary>
    public string SourcePath { get; }

    /// <summary>A mutable copy of the whole file; tamper helpers write into it.</summary>
    public byte[] Bytes { get; private set; }

    /// <summary>The parsed header.</summary>
    public VaultHeader Header { get; }

    /// <summary>The decrypted, validated index.</summary>
    public VaultIndex Index { get; }

    /// <summary>Absolute offset of the first data byte.</summary>
    public long DataSectionOffset => VaultHeader.Size + Header.IndexLength;

    /// <summary>Absolute offset of the primary index ciphertext.</summary>
    public static long IndexOffset => VaultHeader.Size;

    /// <summary>Absolute offset of the index copy at the end of the file.</summary>
    public long IndexCopyOffset => Bytes.LongLength - Header.IndexLength;

    /// <summary>The 16-byte vault id derived from the unwrapped vault key.</summary>
    public byte[] VaultId => VaultKeys.DeriveVaultId(_vaultKey.Span);

    /// <summary>
    /// Opens a vault file for surgery: real Argon2id, real unwrap, real index decryption.
    /// </summary>
    /// <param name="path">Path of the vault file.</param>
    /// <param name="password">Password protecting it.</param>
    /// <param name="keyFileDigest">Keyfile digest, or an empty span when no keyfile is used.</param>
    public static VaultImage Load(string path, string password, ReadOnlySpan<byte> keyFileDigest = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);

        byte[] bytes = System.IO.File.ReadAllBytes(path);
        VaultHeader header = VaultHeader.Parse(bytes, bytes.LongLength);

        using KeyMaterial kek = DeriveKek(password, header, keyFileDigest);
        KeyMaterial vaultKey = HeaderCipher.UnwrapVaultKey(
            kek.Span, header.WrapNonce, header.WrappedVaultKey, header.BuildWrapAad());

        try
        {
            using KeyMaterial indexKey = VaultKeys.DeriveIndexKey(vaultKey.Span);
            byte[] ciphertext = bytes.AsSpan(VaultHeader.Size, (int)header.IndexLength).ToArray();
            byte[] plaintext = HeaderCipher.DecryptIndex(
                indexKey.Span, header.IndexNonce, ciphertext, header.BuildIndexAad());
            try
            {
                return new VaultImage(path, bytes, header, IndexSerializer.Deserialize(plaintext), vaultKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch
        {
            vaultKey.Dispose();
            throw;
        }
    }

    /// <summary>Derives the key-encryption key of a header exactly as <c>Credentials</c> does.</summary>
    /// <param name="password">Password protecting the vault.</param>
    /// <param name="header">The parsed header holding salt and cost parameters.</param>
    /// <param name="keyFileDigest">Keyfile digest, or an empty span.</param>
    public static KeyMaterial DeriveKek(string password, VaultHeader header, ReadOnlySpan<byte> keyFileDigest = default)
    {
        ArgumentNullException.ThrowIfNull(header);

        using Passphrase passphrase = Passphrase.FromString(password);
        byte[] argon2 = Argon2.Instance.DeriveArgon2id(
            passphrase.Bytes, header.KdfSalt, header.Kdf, 32, CancellationToken.None);
        try
        {
            return VaultKeys.DeriveKek(argon2, keyFileDigest, header.KdfSalt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(argon2);
        }
    }

    /// <summary>The index key of this vault; the caller disposes it.</summary>
    public KeyMaterial IndexKey() => VaultKeys.DeriveIndexKey(_vaultKey.Span);

    /// <summary>The blob key of one blob id; the caller disposes it.</summary>
    /// <param name="blobId">The 16-byte blob id.</param>
    public KeyMaterial BlobKey(ReadOnlySpan<byte> blobId) => VaultKeys.DeriveBlobKey(_vaultKey.Span, blobId);

    /// <summary>The index entry of a file, found by its name.</summary>
    /// <param name="name">Entry name, matched exactly.</param>
    public IndexEntry FileEntry(string name) =>
        Index.Entries.Single(entry => entry.Kind == EntryKind.File && entry.Name == name);

    /// <summary>Absolute byte range of a file's blob.</summary>
    /// <param name="name">Entry name.</param>
    public (long Offset, long Length) BlobRange(string name)
    {
        IndexEntry entry = FileEntry(name);
        return (DataSectionOffset + entry.DataOffset, ChunkCipher.BlobLength(entry.Length, entry.ChunkSize));
    }

    /// <summary>Absolute byte range of one chunk of a file's blob, ciphertext plus tag.</summary>
    /// <param name="name">Entry name.</param>
    /// <param name="chunk">Zero-based chunk index.</param>
    public (long Offset, long Length) ChunkRange(string name, uint chunk)
    {
        IndexEntry entry = FileEntry(name);
        uint count = ChunkCipher.ChunkCount(entry.Length, entry.ChunkSize);
        if (chunk >= count)
        {
            throw new ArgumentOutOfRangeException(nameof(chunk), chunk, $"{name} has {count} chunks.");
        }

        long plaintext = chunk == count - 1
            ? entry.Length - ((long)chunk * entry.ChunkSize)
            : entry.ChunkSize;
        long offset = DataSectionOffset + entry.DataOffset + ((long)chunk * (entry.ChunkSize + ChunkCipher.TagSize));
        return (offset, plaintext + ChunkCipher.TagSize);
    }

    /// <summary>Flips bit 6 of one byte of the image.</summary>
    /// <param name="offset">Absolute offset of the byte.</param>
    public void Flip(long offset) => Bytes[offset] ^= 0x40;

    /// <summary>Exchanges two equally long, non-overlapping byte ranges of the image.</summary>
    /// <param name="first">Offset of the first range.</param>
    /// <param name="second">Offset of the second range.</param>
    /// <param name="length">Length of both ranges.</param>
    public void Swap(long first, long second, long length)
    {
        byte[] buffer = Bytes.AsSpan((int)first, (int)length).ToArray();
        Bytes.AsSpan((int)second, (int)length).CopyTo(Bytes.AsSpan((int)first));
        buffer.CopyTo(Bytes.AsSpan((int)second));
    }

    /// <summary>Overwrites a byte range of the image.</summary>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="content">Replacement bytes.</param>
    public void Overwrite(long offset, ReadOnlySpan<byte> content) => content.CopyTo(Bytes.AsSpan((int)offset));

    /// <summary>Replaces the whole image with different bytes (truncation, appending).</summary>
    /// <param name="bytes">The new content.</param>
    public void Replace(byte[] bytes) => Bytes = bytes;

    /// <summary>Writes the current image over a file, replacing it even while a session holds it open.</summary>
    /// <param name="destination">Path to write to.</param>
    public void WriteTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (System.IO.File.Exists(destination))
        {
            System.IO.File.Delete(destination);
        }

        System.IO.File.WriteAllBytes(destination, Bytes);
    }

    /// <summary>
    /// Builds a complete vault file around a hand-crafted index plaintext: a fresh header with the same
    /// key wrap and new index nonces, the crafted index encrypted under the real index key, a data
    /// section of the declared length and the matching index copy.
    /// </summary>
    /// <param name="paddedPlaintext">The crafted, already padded index plaintext.</param>
    /// <param name="dataSection">The data section to write; zero-filled to its declared length when shorter.</param>
    /// <param name="declaredDataSectionLength">Data section length the crafted index declares.</param>
    public byte[] BuildAroundIndex(byte[] paddedPlaintext, byte[]? dataSection, long declaredDataSectionLength)
    {
        ArgumentNullException.ThrowIfNull(paddedPlaintext);

        var header = new VaultHeader
        {
            FormatVersion = Header.FormatVersion,
            Flags = Header.Flags,
            Kdf = Header.Kdf,
            KdfSalt = Header.KdfSalt,
            WrapNonce = Header.WrapNonce,
            WrappedVaultKey = Header.WrappedVaultKey,
            IndexNonce = Repeat(0x51, 12),
            IndexCopyNonce = Repeat(0x52, 12),
            IndexLength = paddedPlaintext.LongLength + ChunkCipher.TagSize,
        };

        using KeyMaterial indexKey = IndexKey();
        byte[] indexAad = header.BuildIndexAad();
        byte[] primary = HeaderCipher.EncryptIndex(indexKey.Span, header.IndexNonce, paddedPlaintext, indexAad);
        byte[] copy = HeaderCipher.EncryptIndex(indexKey.Span, header.IndexCopyNonce, paddedPlaintext, indexAad);

        byte[] data = new byte[Math.Max(declaredDataSectionLength, 0)];
        dataSection?.AsSpan(0, Math.Min(dataSection.Length, data.Length)).CopyTo(data);

        byte[] file = new byte[VaultHeader.Size + primary.LongLength + data.LongLength + copy.LongLength];
        header.Write(file);
        primary.CopyTo(file.AsSpan(VaultHeader.Size));
        data.CopyTo(file.AsSpan(VaultHeader.Size + primary.Length));
        copy.CopyTo(file.AsSpan(VaultHeader.Size + primary.Length + data.Length));
        return file;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vaultKey.Dispose();
    }

    /// <summary>Builds a byte array of one repeated value.</summary>
    /// <param name="value">The value.</param>
    /// <param name="count">Number of bytes.</param>
    private static byte[] Repeat(byte value, int count)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    /// <summary>The absolute path of a sibling file in the same directory.</summary>
    /// <param name="path">Reference path.</param>
    /// <param name="name">File name of the sibling.</param>
    public static string Sibling(string path, string name) =>
        IoPath.Combine(IoPath.GetDirectoryName(IoPath.GetFullPath(path))!, name);
}
