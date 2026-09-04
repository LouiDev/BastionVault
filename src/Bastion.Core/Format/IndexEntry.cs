namespace Bastion.Core.Format;

/// <summary>
/// One serialized index entry (FORMAT.md section 4.4). This is the wire shape, not a domain object:
/// plain mutable fields, written and read by <see cref="IndexSerializer"/>.
/// </summary>
public sealed class IndexEntry
{
    /// <summary>Folder or file.</summary>
    public EntryKind Kind;

    /// <summary>Entry id: 1 .. 0xFFFFFFFE, unique within the index and less than <see cref="VaultIndex.NextEntryId"/>.</summary>
    public uint Id;

    /// <summary>Parent id: 0 for the root, otherwise a folder that appears earlier in the array.</summary>
    public uint ParentId;

    /// <summary>Entry name; strict UTF-8 on disk, 1 .. 765 bytes, valid per FORMAT.md section 6.1.</summary>
    public string Name = "";

    /// <summary>Creation time as <see cref="DateTime"/> ticks (UTC); out-of-range values are clamped to 0 on read.</summary>
    public long CreatedUtcTicks;

    /// <summary>Modification time as <see cref="DateTime"/> ticks (UTC); out-of-range values are clamped to 0 on read.</summary>
    public long ModifiedUtcTicks;

    /// <summary>Reserved attribute bits; 0 in v1.</summary>
    public uint Attributes;

    /// <summary>Comment; strict UTF-8, 0 .. 4096 bytes, no C0/C1 controls except TAB, LF and CR.</summary>
    public string Comment = "";

    /// <summary>File entries only: the 16-byte blob id, unique within the index.</summary>
    public byte[]? BlobId;

    /// <summary>File entries only: offset of the blob relative to the start of the data section.</summary>
    public long DataOffset;

    /// <summary>File entries only: plaintext length in bytes, at most 2^48 - 1.</summary>
    public long Length;

    /// <summary>File entries only: chunk size, a power of two in 64 KiB .. 64 MiB.</summary>
    public uint ChunkSize;

    /// <summary>File entries only: SHA-256 over the whole blob ciphertext (FORMAT.md section 2.8).</summary>
    public byte[]? BlobHash;
}
