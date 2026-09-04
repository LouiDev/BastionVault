using System.Buffers.Binary;
using System.Text;
using BastionVault.Core;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Format;

/// <summary>
/// Writes index plaintext exactly as FORMAT.md section 4.3 and 4.4 describe it, but without any
/// validation, so a test can produce a buffer that violates one single rule of section 4.6.
/// </summary>
internal sealed class IndexPlaintextBuilder
{
    /// <summary>Index version field.</summary>
    public uint IndexVersion { get; set; } = 1;

    /// <summary>Save counter field.</summary>
    public ulong SaveCounter { get; set; } = 1;

    /// <summary>Save timestamp field.</summary>
    public long SavedUtcTicks { get; set; }

    /// <summary>Declared data section length.</summary>
    public ulong DataSectionLength { get; set; }

    /// <summary>Declared trailing random padding of the data section.</summary>
    public ulong DataPaddingLength { get; set; }

    /// <summary>Next id to allocate.</summary>
    public uint NextEntryId { get; set; } = 1;

    /// <summary>Written instead of the real entry count when set.</summary>
    public uint? EntryCountOverride { get; set; }

    /// <summary>Written instead of the real unpadded length when set.</summary>
    public uint? UnpaddedLengthOverride { get; set; }

    /// <summary>Padded length to produce; defaults to <see cref="PadLadder.Index(long)"/>.</summary>
    public int? PaddedLengthOverride { get; set; }

    /// <summary>Value the padding is filled with; the format requires zero.</summary>
    public byte PaddingByte { get; set; }

    /// <summary>The entries, written in the order they appear here.</summary>
    public List<RawEntry> Entries { get; } = [];

    /// <summary>Adds a folder entry.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="parentId">Parent id, 0 for the root.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="comment">Entry comment.</param>
    public RawEntry AddFolder(uint id, uint parentId, string name, string comment = "")
    {
        var entry = new RawEntry
        {
            Kind = 0,
            Id = id,
            ParentId = parentId,
            Name = Encoding.UTF8.GetBytes(name),
            Comment = Encoding.UTF8.GetBytes(comment),
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Adds a file entry.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="parentId">Parent id, 0 for the root.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="dataOffset">Blob offset inside the data section.</param>
    /// <param name="length">Plaintext length.</param>
    /// <param name="chunkSize">Chunk size.</param>
    /// <param name="blobSeed">Byte the blob id and hash are filled with.</param>
    public RawEntry AddFile(uint id, uint parentId, string name, ulong dataOffset, ulong length, uint chunkSize = 65536, byte blobSeed = 1)
    {
        var entry = new RawEntry
        {
            Kind = 1,
            Id = id,
            ParentId = parentId,
            Name = Encoding.UTF8.GetBytes(name),
            Comment = [],
            BlobId = Fill(16, blobSeed),
            BlobHash = Fill(32, blobSeed),
            DataOffset = dataOffset,
            Length = length,
            ChunkSize = chunkSize,
        };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Serializes the configured fields, padding the result like a conforming writer would.</summary>
    public byte[] Build()
    {
        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(IndexVersion);
            writer.Write(0u);                       // unpaddedLength placeholder
            writer.Write(SaveCounter);
            writer.Write(SavedUtcTicks);
            writer.Write(DataSectionLength);
            writer.Write(DataPaddingLength);
            writer.Write(NextEntryId);
            writer.Write(EntryCountOverride ?? (uint)Entries.Count);

            foreach (RawEntry entry in Entries)
            {
                writer.Write(entry.Kind);
                writer.Write(entry.Id);
                writer.Write(entry.ParentId);
                writer.Write(entry.NameLengthOverride ?? (ushort)entry.Name.Length);
                writer.Write(entry.Name);
                writer.Write(entry.CreatedUtcTicks);
                writer.Write(entry.ModifiedUtcTicks);
                writer.Write(entry.Attributes);
                writer.Write(entry.CommentLengthOverride ?? (ushort)entry.Comment.Length);
                writer.Write(entry.Comment);

                if (!(entry.WriteFileTail ?? entry.Kind == 1))
                {
                    continue;
                }

                writer.Write(entry.BlobId ?? new byte[16]);
                writer.Write(entry.DataOffset);
                writer.Write(entry.Length);
                writer.Write(entry.ChunkSize);
                writer.Write(entry.BlobHash ?? new byte[32]);
            }
        }

        byte[] unpadded = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(unpadded.AsSpan(4), UnpaddedLengthOverride ?? (uint)unpadded.Length);

        int paddedLength = PaddedLengthOverride ?? (int)PadLadder.Index(unpadded.Length);
        byte[] padded = new byte[Math.Max(paddedLength, 0)];
        if (PaddingByte != 0)
        {
            Array.Fill(padded, PaddingByte);
        }

        unpadded.AsSpan(0, Math.Min(unpadded.Length, padded.Length)).CopyTo(padded);
        return padded;
    }

    /// <summary>An index that satisfies every rule: one folder holding one file.</summary>
    public static IndexPlaintextBuilder Valid()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 116 };
        builder.AddFolder(1, 0, "Docs");
        builder.AddFile(2, 1, "a.txt", dataOffset: 0, length: 100);
        return builder;
    }

    /// <summary>Builds a byte array of one repeated value.</summary>
    /// <param name="count">Number of bytes.</param>
    /// <param name="value">Value to repeat.</param>
    public static byte[] Fill(int count, byte value)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    /// <summary>Ciphertext length of a blob: plaintext plus one tag per chunk.</summary>
    /// <param name="length">Plaintext length.</param>
    /// <param name="chunkSize">Chunk size.</param>
    public static ulong BlobLength(ulong length, uint chunkSize)
    {
        ulong chunks = Math.Max(1, (length + chunkSize - 1) / chunkSize);
        return length + (16 * chunks);
    }
}

/// <summary>One entry as it goes on the wire, with every field independently settable.</summary>
internal sealed class RawEntry
{
    /// <summary>Kind byte: 0 folder, 1 file.</summary>
    public byte Kind { get; set; }

    /// <summary>Entry id.</summary>
    public uint Id { get; set; }

    /// <summary>Parent id.</summary>
    public uint ParentId { get; set; }

    /// <summary>Raw name bytes, written verbatim.</summary>
    public byte[] Name { get; set; } = [];

    /// <summary>Written instead of the real name length when set.</summary>
    public ushort? NameLengthOverride { get; set; }

    /// <summary>Creation timestamp field.</summary>
    public long CreatedUtcTicks { get; set; }

    /// <summary>Modification timestamp field.</summary>
    public long ModifiedUtcTicks { get; set; }

    /// <summary>Attribute bits; version 1 requires 0.</summary>
    public uint Attributes { get; set; }

    /// <summary>Raw comment bytes, written verbatim.</summary>
    public byte[] Comment { get; set; } = [];

    /// <summary>Written instead of the real comment length when set.</summary>
    public ushort? CommentLengthOverride { get; set; }

    /// <summary>Blob id of a file entry.</summary>
    public byte[]? BlobId { get; set; }

    /// <summary>Blob offset of a file entry.</summary>
    public ulong DataOffset { get; set; }

    /// <summary>Plaintext length of a file entry.</summary>
    public ulong Length { get; set; }

    /// <summary>Chunk size of a file entry.</summary>
    public uint ChunkSize { get; set; }

    /// <summary>Blob commitment hash of a file entry.</summary>
    public byte[]? BlobHash { get; set; }

    /// <summary>Forces the file tail to be written or omitted regardless of <see cref="Kind"/>.</summary>
    public bool? WriteFileTail { get; set; }
}
