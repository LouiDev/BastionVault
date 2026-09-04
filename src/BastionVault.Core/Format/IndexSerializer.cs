using System.Buffers.Binary;
using System.Text;

namespace BastionVault.Core.Format;

/// <summary>
/// Reads and writes the index plaintext of FORMAT.md section 4.3 and 4.4. Two conforming writers
/// produce identical bytes for the same tree.
/// </summary>
public static class IndexSerializer
{
    /// <summary>The only index version defined in v1.</summary>
    private const uint IndexVersion = 1;

    /// <summary>Size of the fixed field block that precedes the entry array (FORMAT.md section 4.3).</summary>
    private const int FixedBlockSize = 48;

    /// <summary>Size of the fixed part of an entry before the name (kind, id, parentId, nameLen).</summary>
    private const int EntryHeadSize = 11;

    /// <summary>Size of the fixed part between name and comment (timestamps, attributes, commentLen).</summary>
    private const int EntryMiddleSize = 22;

    /// <summary>Size of the file-only tail (blobId, dataOffset, length, chunkSize, blobHash).</summary>
    private const int FileTailSize = 68;

    /// <summary>Length of a blob id in bytes.</summary>
    private const int BlobIdSize = 16;

    /// <summary>Length of a blob commitment hash in bytes.</summary>
    private const int BlobHashSize = 32;

    /// <summary>Largest representable <see cref="DateTime"/> tick count; anything else reads as 0.</summary>
    private const long MaxTicks = 3155378975999999999L;

    /// <summary>UTF-8 that rejects invalid sequences in both directions.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Serializes an index in canonical order (section 4.5) and zero-pads it to
    /// <see cref="PadLadder.Index(long)"/>.
    /// </summary>
    /// <param name="index">The index to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is <see langword="null"/>.</exception>
    /// <exception cref="VaultFormatException"><see cref="VaultErrorCode.IndexInvalid"/> when the tree violates section 4.6.</exception>
    public static byte[] Serialize(VaultIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        List<WireEntry> ordered = ValidateAndOrder(index);

        long unpaddedLength = FixedBlockSize;
        foreach (WireEntry entry in ordered)
        {
            unpaddedLength += EntryHeadSize + entry.Name.Length + EntryMiddleSize + entry.Comment.Length;
            if (entry.Entry.Kind == EntryKind.File)
            {
                unpaddedLength += FileTailSize;
            }
        }

        if (unpaddedLength > VaultLimits.MaxIndexPlaintext)
        {
            throw Invalid($"The index needs {unpaddedLength} bytes; the format allows at most {VaultLimits.MaxIndexPlaintext}.");
        }

        long paddedLength = PadLadder.Index(unpaddedLength);
        if (paddedLength > VaultLimits.MaxIndexPlaintext)
        {
            throw Invalid($"The padded index would be {paddedLength} bytes; the format allows at most {VaultLimits.MaxIndexPlaintext}.");
        }

        byte[] buffer = new byte[paddedLength];
        Span<byte> cursor = buffer;
        int position = 0;

        WriteUInt32(cursor, ref position, IndexVersion);
        WriteUInt32(cursor, ref position, (uint)unpaddedLength);
        WriteUInt64(cursor, ref position, index.SaveCounter);
        WriteInt64(cursor, ref position, index.SavedUtcTicks);
        WriteUInt64(cursor, ref position, (ulong)index.DataSectionLength);
        WriteUInt64(cursor, ref position, (ulong)index.DataPaddingLength);
        WriteUInt32(cursor, ref position, index.NextEntryId);
        WriteUInt32(cursor, ref position, (uint)ordered.Count);

        foreach (WireEntry wire in ordered)
        {
            IndexEntry entry = wire.Entry;
            cursor[position++] = (byte)entry.Kind;
            WriteUInt32(cursor, ref position, entry.Id);
            WriteUInt32(cursor, ref position, entry.ParentId);
            WriteUInt16(cursor, ref position, (ushort)wire.Name.Length);
            wire.Name.AsSpan().CopyTo(cursor[position..]);
            position += wire.Name.Length;
            WriteInt64(cursor, ref position, entry.CreatedUtcTicks);
            WriteInt64(cursor, ref position, entry.ModifiedUtcTicks);
            WriteUInt32(cursor, ref position, entry.Attributes);
            WriteUInt16(cursor, ref position, (ushort)wire.Comment.Length);
            wire.Comment.AsSpan().CopyTo(cursor[position..]);
            position += wire.Comment.Length;

            if (entry.Kind != EntryKind.File)
            {
                continue;
            }

            entry.BlobId!.AsSpan().CopyTo(cursor[position..]);
            position += BlobIdSize;
            WriteUInt64(cursor, ref position, (ulong)entry.DataOffset);
            WriteUInt64(cursor, ref position, (ulong)entry.Length);
            WriteUInt32(cursor, ref position, entry.ChunkSize);
            entry.BlobHash!.AsSpan().CopyTo(cursor[position..]);
            position += BlobHashSize;
        }

        if (position != unpaddedLength)
        {
            throw Invalid($"Internal length mismatch: wrote {position} bytes, expected {unpaddedLength}.");
        }

        return buffer;
    }

    /// <summary>
    /// Parses and fully validates an index. Every rule of section 4.6 is checked over the whole entry
    /// array before any node is exposed, in checked arithmetic, never slicing on an unverified length.
    /// </summary>
    /// <param name="paddedPlaintext">The decrypted, padded index plaintext.</param>
    /// <exception cref="VaultFormatException">
    /// <see cref="VaultErrorCode.IndexInvalid"/>. This method never throws anything else.
    /// </exception>
    public static VaultIndex Deserialize(ReadOnlySpan<byte> paddedPlaintext)
    {
        try
        {
            return Read(paddedPlaintext);
        }
        catch (VaultFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defence in depth: no code path below is expected to throw anything else, but a reader of
            // hostile input must never surface an unexpected exception type.
            throw new VaultFormatException(VaultErrorCode.IndexInvalid, "The index could not be read.", ex);
        }
    }

    /// <summary>Reads and validates the index plaintext.</summary>
    /// <param name="paddedPlaintext">The decrypted, padded index plaintext.</param>
    private static VaultIndex Read(ReadOnlySpan<byte> paddedPlaintext)
    {
        checked
        {
            if (paddedPlaintext.Length > VaultLimits.MaxIndexPlaintext)
            {
                throw Invalid($"The index plaintext is {paddedPlaintext.Length} bytes; at most {VaultLimits.MaxIndexPlaintext} are allowed.");
            }

            var head = new Cursor(paddedPlaintext);
            if (!head.TryReadUInt32(out uint indexVersion) ||
                !head.TryReadUInt32(out uint unpaddedLength) ||
                !head.TryReadUInt64(out ulong saveCounter) ||
                !head.TryReadInt64(out long savedUtcTicks) ||
                !head.TryReadUInt64(out ulong dataSectionLength) ||
                !head.TryReadUInt64(out ulong dataPaddingLength) ||
                !head.TryReadUInt32(out uint nextEntryId) ||
                !head.TryReadUInt32(out uint entryCount))
            {
                throw Invalid($"The index plaintext is {paddedPlaintext.Length} bytes; the fixed field block alone needs {FixedBlockSize}.");
            }

            if (indexVersion != IndexVersion)
            {
                throw Invalid($"The index declares version {indexVersion}; this build reads version {IndexVersion}.");
            }

            if (unpaddedLength < FixedBlockSize)
            {
                throw Invalid($"unpaddedLength is {unpaddedLength}; it must cover at least the {FixedBlockSize}-byte field block.");
            }

            if (unpaddedLength > (ulong)paddedPlaintext.Length)
            {
                throw Invalid($"unpaddedLength is {unpaddedLength} but only {paddedPlaintext.Length} bytes are available.");
            }

            if (entryCount > VaultLimits.MaxEntries)
            {
                throw Invalid($"The index claims {entryCount} entries; at most {VaultLimits.MaxEntries} are allowed.");
            }

            if (dataSectionLength > long.MaxValue || dataPaddingLength > long.MaxValue)
            {
                throw Invalid("The data section length is larger than a file can be.");
            }

            if (dataPaddingLength > dataSectionLength)
            {
                throw Invalid($"dataPaddingLength ({dataPaddingLength}) exceeds dataSectionLength ({dataSectionLength}).");
            }

            // Everything after unpaddedLength must be zero, and the entry array must end exactly there.
            ReadOnlySpan<byte> padding = paddedPlaintext[(int)unpaddedLength..];
            if (padding.ContainsAnyExcept((byte)0))
            {
                throw Invalid("The index padding contains a non-zero byte.");
            }

            var body = new Cursor(paddedPlaintext[..(int)unpaddedLength]);
            body.Skip(FixedBlockSize);

            var index = new VaultIndex
            {
                SaveCounter = saveCounter,
                SavedUtcTicks = ClampTicks(savedUtcTicks),
                DataSectionLength = (long)dataSectionLength,
                DataPaddingLength = (long)dataPaddingLength,
                NextEntryId = nextEntryId,
                Entries = [],
            };

            var seenIds = new HashSet<uint>();
            var folderDepths = new Dictionary<uint, int>();
            var siblingNames = new HashSet<(uint ParentId, string Name)>(SiblingNameComparer.Instance);
            var blobIds = new HashSet<(ulong Low, ulong High)>();
            var blobs = new List<(long Offset, long Length)>();

            for (uint i = 0; i < entryCount; i++)
            {
                IndexEntry entry = ReadEntry(ref body, i, nextEntryId, seenIds, folderDepths, siblingNames, blobIds, blobs);
                index.Entries.Add(entry);
            }

            if (body.Remaining != 0)
            {
                throw Invalid($"The entry array ends {body.Remaining} bytes before unpaddedLength ({unpaddedLength}).");
            }

            CheckTiling(blobs, (long)dataSectionLength, (long)dataPaddingLength);
            return index;
        }
    }

    /// <summary>Reads one entry and applies every per-entry rule of section 4.6.</summary>
    /// <param name="body">Cursor over the meaningful part of the plaintext.</param>
    /// <param name="ordinal">Zero-based position in the entry array, for error messages.</param>
    /// <param name="nextEntryId">The index-wide next id; every id must be smaller.</param>
    /// <param name="seenIds">Ids seen so far.</param>
    /// <param name="folderDepths">Depth of every folder seen so far, keyed by id.</param>
    /// <param name="siblingNames">Names seen so far, keyed by parent.</param>
    /// <param name="blobIds">Blob ids seen so far.</param>
    /// <param name="blobs">Offset and ciphertext length of every blob seen so far.</param>
    private static IndexEntry ReadEntry(
        ref Cursor body,
        uint ordinal,
        uint nextEntryId,
        HashSet<uint> seenIds,
        Dictionary<uint, int> folderDepths,
        HashSet<(uint ParentId, string Name)> siblingNames,
        HashSet<(ulong Low, ulong High)> blobIds,
        List<(long Offset, long Length)> blobs)
    {
        checked
        {
            if (!body.TryReadByte(out byte kindByte) ||
                !body.TryReadUInt32(out uint id) ||
                !body.TryReadUInt32(out uint parentId) ||
                !body.TryReadUInt16(out ushort nameLength))
            {
                throw Invalid($"Entry {ordinal} is truncated.");
            }

            if (kindByte is not ((byte)EntryKind.Folder) and not ((byte)EntryKind.File))
            {
                throw Invalid($"Entry {ordinal} has kind {kindByte}; only 0 (folder) and 1 (file) exist.");
            }

            var kind = (EntryKind)kindByte;

            if (id == 0 || id == uint.MaxValue)
            {
                throw Invalid($"Entry {ordinal} has the reserved id {id}.");
            }

            if (id >= nextEntryId)
            {
                throw Invalid($"Entry {ordinal} has id {id}, which is not below nextEntryId ({nextEntryId}).");
            }

            if (!seenIds.Add(id))
            {
                throw Invalid($"Entry {ordinal} repeats id {id}.");
            }

            int depth;
            if (parentId == 0)
            {
                depth = 1;
            }
            else if (folderDepths.TryGetValue(parentId, out int parentDepth))
            {
                depth = parentDepth + 1;
            }
            else
            {
                throw Invalid($"Entry {ordinal} names parent {parentId}, which is not a folder that appeared earlier.");
            }

            if (depth > VaultLimits.MaxDepth)
            {
                throw Invalid($"Entry {ordinal} sits at depth {depth}; the format allows {VaultLimits.MaxDepth}.");
            }

            if (nameLength is < VaultLimits.MinNameCodeUnits or > VaultLimits.MaxNameBytes)
            {
                throw Invalid($"Entry {ordinal} has a name of {nameLength} bytes; 1 to {VaultLimits.MaxNameBytes} are allowed.");
            }

            if (!body.TryReadBytes(nameLength, out ReadOnlySpan<byte> nameBytes))
            {
                throw Invalid($"Entry {ordinal} is truncated inside its name.");
            }

            string name = DecodeUtf8(nameBytes, ordinal, "name");
            NameCheck check = EntryNames.Validate(name);
            if (!check.IsValid)
            {
                throw Invalid($"Entry {ordinal} has an invalid name: {check.Reason}");
            }

            if (!siblingNames.Add((parentId, name)))
            {
                throw Invalid($"Two entries under parent {parentId} share a name (compared case-insensitively).");
            }

            if (!body.TryReadInt64(out long createdTicks) ||
                !body.TryReadInt64(out long modifiedTicks) ||
                !body.TryReadUInt32(out uint attributes) ||
                !body.TryReadUInt16(out ushort commentLength))
            {
                throw Invalid($"Entry {ordinal} is truncated after its name.");
            }

            if (attributes != 0)
            {
                throw Invalid($"Entry {ordinal} sets attribute bits 0x{attributes:X8}; version 1 defines none.");
            }

            if (commentLength > VaultLimits.MaxCommentBytes)
            {
                throw Invalid($"Entry {ordinal} has a comment of {commentLength} bytes; at most {VaultLimits.MaxCommentBytes} are allowed.");
            }

            if (!body.TryReadBytes(commentLength, out ReadOnlySpan<byte> commentBytes))
            {
                throw Invalid($"Entry {ordinal} is truncated inside its comment.");
            }

            string comment = DecodeUtf8(commentBytes, ordinal, "comment");
            if (FindControlCharacter(comment) is char bad)
            {
                throw Invalid($"Entry {ordinal} has a comment containing the disallowed character U+{(int)bad:X4}.");
            }

            var entry = new IndexEntry
            {
                Kind = kind,
                Id = id,
                ParentId = parentId,
                Name = name,
                CreatedUtcTicks = ClampTicks(createdTicks),
                ModifiedUtcTicks = ClampTicks(modifiedTicks),
                Attributes = attributes,
                Comment = comment,
            };

            if (kind == EntryKind.Folder)
            {
                folderDepths[id] = depth;
                return entry;
            }

            if (!body.TryReadBytes(BlobIdSize, out ReadOnlySpan<byte> blobId) ||
                !body.TryReadUInt64(out ulong dataOffset) ||
                !body.TryReadUInt64(out ulong length) ||
                !body.TryReadUInt32(out uint chunkSize) ||
                !body.TryReadBytes(BlobHashSize, out ReadOnlySpan<byte> blobHash))
            {
                throw Invalid($"Entry {ordinal} is truncated inside its file fields.");
            }

            if (!blobIds.Add((BinaryPrimitives.ReadUInt64LittleEndian(blobId), BinaryPrimitives.ReadUInt64LittleEndian(blobId[8..]))))
            {
                throw Invalid($"Entry {ordinal} repeats a blob id that another entry already uses.");
            }

            if (length > (ulong)VaultLimits.MaxFileLength)
            {
                throw Invalid($"Entry {ordinal} declares a length of {length} bytes; at most {VaultLimits.MaxFileLength} are allowed.");
            }

            if (chunkSize < VaultLimits.MinChunkSize || chunkSize > VaultLimits.MaxChunkSize || !uint.IsPow2(chunkSize))
            {
                throw Invalid(
                    $"Entry {ordinal} declares a chunk size of {chunkSize}; it must be a power of two between " +
                    $"{VaultLimits.MinChunkSize} and {VaultLimits.MaxChunkSize}.");
            }

            long plaintextLength = (long)length;
            long chunkCount = Math.Max(1, ((plaintextLength + chunkSize) - 1) / chunkSize);
            if (chunkCount > VaultLimits.MaxChunkCount)
            {
                throw Invalid($"Entry {ordinal} would need {chunkCount} chunks; at most {VaultLimits.MaxChunkCount} are allowed.");
            }

            if (dataOffset > long.MaxValue)
            {
                throw Invalid($"Entry {ordinal} declares a data offset beyond the end of any file.");
            }

            long blobLength = plaintextLength + (VaultLimits.TagSize * chunkCount);
            blobs.Add(((long)dataOffset, blobLength));

            entry.BlobId = blobId.ToArray();
            entry.DataOffset = (long)dataOffset;
            entry.Length = plaintextLength;
            entry.ChunkSize = chunkSize;
            entry.BlobHash = blobHash.ToArray();
            return entry;
        }
    }

    /// <summary>Rule 9: the blobs, sorted by offset, must tile the content part of the data section exactly.</summary>
    /// <param name="blobs">Offset and ciphertext length of every blob.</param>
    /// <param name="dataSectionLength">Declared data section length.</param>
    /// <param name="dataPaddingLength">Declared trailing random padding.</param>
    private static void CheckTiling(List<(long Offset, long Length)> blobs, long dataSectionLength, long dataPaddingLength)
    {
        checked
        {
            long contentLength = dataSectionLength - dataPaddingLength;
            if (blobs.Count == 0)
            {
                if (contentLength != 0)
                {
                    throw Invalid($"The index holds no blobs but reserves {contentLength} bytes of content.");
                }

                return;
            }

            blobs.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

            long expected = 0;
            foreach ((long offset, long length) in blobs)
            {
                if (offset != expected)
                {
                    throw Invalid(
                        offset < expected
                            ? $"A blob at offset {offset} overlaps the previous blob, which ends at {expected}."
                            : $"A blob at offset {offset} leaves a gap; the previous blob ends at {expected}.");
                }

                expected = offset + length;
                if (expected > contentLength)
                {
                    throw Invalid($"A blob ends at {expected}, past the end of the content area ({contentLength}).");
                }
            }

            if (expected != contentLength)
            {
                throw Invalid($"The blobs end at {expected} but the content area is {contentLength} bytes long.");
            }
        }
    }

    /// <summary>Validates a whole in-memory index and returns its entries in canonical order (section 4.5).</summary>
    /// <param name="index">The index to write.</param>
    private static List<WireEntry> ValidateAndOrder(VaultIndex index)
    {
        checked
        {
            List<IndexEntry> entries = index.Entries ?? throw Invalid("The index has no entry list.");
            if (entries.Count > VaultLimits.MaxEntries)
            {
                throw Invalid($"The index holds {entries.Count} entries; at most {VaultLimits.MaxEntries} are allowed.");
            }

            if (index.DataSectionLength < 0 || index.DataPaddingLength < 0)
            {
                throw Invalid("The data section length and its padding must not be negative.");
            }

            if (index.DataPaddingLength > index.DataSectionLength)
            {
                throw Invalid(
                    $"dataPaddingLength ({index.DataPaddingLength}) exceeds dataSectionLength ({index.DataSectionLength}).");
            }

            var seenIds = new HashSet<uint>();
            var children = new Dictionary<uint, List<IndexEntry>>();
            var blobIds = new HashSet<(ulong Low, ulong High)>();
            var siblingNames = new HashSet<(uint ParentId, string Name)>(SiblingNameComparer.Instance);
            var blobs = new List<(long Offset, long Length)>();

            for (int i = 0; i < entries.Count; i++)
            {
                IndexEntry entry = entries[i] ?? throw Invalid($"Entry {i} is null.");
                if (entry.Id == 0 || entry.Id == uint.MaxValue)
                {
                    throw Invalid($"Entry {i} has the reserved id {entry.Id}.");
                }

                if (entry.Id >= index.NextEntryId)
                {
                    throw Invalid($"Entry {i} has id {entry.Id}, which is not below nextEntryId ({index.NextEntryId}).");
                }

                if (!seenIds.Add(entry.Id))
                {
                    throw Invalid($"Entry {i} repeats id {entry.Id}.");
                }

                if (entry.Kind is not EntryKind.Folder and not EntryKind.File)
                {
                    throw Invalid($"Entry {i} has kind {(byte)entry.Kind}; only 0 (folder) and 1 (file) exist.");
                }

                if (entry.Attributes != 0)
                {
                    throw Invalid($"Entry {i} sets attribute bits 0x{entry.Attributes:X8}; version 1 defines none.");
                }

                NameCheck check = EntryNames.Validate(entry.Name ?? string.Empty);
                if (!check.IsValid)
                {
                    throw Invalid($"Entry {i} has an invalid name: {check.Reason}");
                }

                if (!siblingNames.Add((entry.ParentId, entry.Name!)))
                {
                    throw Invalid($"Two entries under parent {entry.ParentId} share a name (compared case-insensitively).");
                }

                if (!children.TryGetValue(entry.ParentId, out List<IndexEntry>? bucket))
                {
                    bucket = [];
                    children[entry.ParentId] = bucket;
                }

                bucket.Add(entry);
            }

            var ordered = new List<WireEntry>();
            var stack = new Stack<(IndexEntry Entry, int Depth)>();
            PushChildren(stack, children, 0, 1);

            while (stack.Count > 0)
            {
                (IndexEntry entry, int depth) = stack.Pop();
                if (depth > VaultLimits.MaxDepth)
                {
                    throw Invalid($"Entry {entry.Id} sits at depth {depth}; the format allows {VaultLimits.MaxDepth}.");
                }

                ordered.Add(BuildWireEntry(entry, blobIds, blobs));

                if (entry.Kind == EntryKind.Folder)
                {
                    PushChildren(stack, children, entry.Id, depth + 1);
                }
                else if (children.ContainsKey(entry.Id))
                {
                    throw Invalid($"Entry {entry.Id} is a file but other entries name it as their parent.");
                }
            }

            if (ordered.Count != entries.Count)
            {
                throw Invalid(
                    $"Only {ordered.Count} of {entries.Count} entries are reachable from the root; " +
                    "a parent is missing, is a file, or the entries form a cycle.");
            }

            CheckTiling(blobs, index.DataSectionLength, index.DataPaddingLength);
            return ordered;
        }
    }

    /// <summary>Pushes the children of one folder so that the pre-order walk pops them by ascending id.</summary>
    /// <param name="stack">Depth-first stack.</param>
    /// <param name="children">Children of every parent id.</param>
    /// <param name="parentId">Parent whose children are pushed.</param>
    /// <param name="depth">Depth the children sit at.</param>
    private static void PushChildren(
        Stack<(IndexEntry Entry, int Depth)> stack,
        Dictionary<uint, List<IndexEntry>> children,
        uint parentId,
        int depth)
    {
        if (!children.TryGetValue(parentId, out List<IndexEntry>? bucket))
        {
            return;
        }

        bucket.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        for (int i = bucket.Count - 1; i >= 0; i--)
        {
            stack.Push((bucket[i], depth));
        }
    }

    /// <summary>Encodes the variable-length fields of one entry and validates its file fields.</summary>
    /// <param name="entry">Entry to encode.</param>
    /// <param name="blobIds">Blob ids seen so far.</param>
    /// <param name="blobs">Offset and ciphertext length of every blob seen so far.</param>
    private static WireEntry BuildWireEntry(
        IndexEntry entry,
        HashSet<(ulong Low, ulong High)> blobIds,
        List<(long Offset, long Length)> blobs)
    {
        checked
        {
            byte[] name = Encode(entry.Name!, entry.Id, "name");
            if (name.Length is < VaultLimits.MinNameCodeUnits or > VaultLimits.MaxNameBytes)
            {
                throw Invalid($"Entry {entry.Id} has a name of {name.Length} bytes; 1 to {VaultLimits.MaxNameBytes} are allowed.");
            }

            string commentText = entry.Comment ?? string.Empty;
            if (FindControlCharacter(commentText) is char bad)
            {
                throw Invalid($"Entry {entry.Id} has a comment containing the disallowed character U+{(int)bad:X4}.");
            }

            byte[] comment = Encode(commentText, entry.Id, "comment");
            if (comment.Length > VaultLimits.MaxCommentBytes)
            {
                throw Invalid($"Entry {entry.Id} has a comment of {comment.Length} bytes; at most {VaultLimits.MaxCommentBytes} are allowed.");
            }

            if (entry.Kind != EntryKind.File)
            {
                return new WireEntry(entry, name, comment);
            }

            if (entry.BlobId is not { Length: BlobIdSize })
            {
                throw Invalid($"File entry {entry.Id} has no {BlobIdSize}-byte blob id.");
            }

            if (entry.BlobHash is not { Length: BlobHashSize })
            {
                throw Invalid($"File entry {entry.Id} has no {BlobHashSize}-byte blob hash.");
            }

            if (!blobIds.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry.BlobId), BinaryPrimitives.ReadUInt64LittleEndian(entry.BlobId.AsSpan(8)))))
            {
                throw Invalid($"File entry {entry.Id} repeats a blob id that another entry already uses.");
            }

            if (entry.Length < 0 || entry.Length > VaultLimits.MaxFileLength)
            {
                throw Invalid($"File entry {entry.Id} declares a length of {entry.Length} bytes; 0 to {VaultLimits.MaxFileLength} are allowed.");
            }

            if (entry.ChunkSize < VaultLimits.MinChunkSize || entry.ChunkSize > VaultLimits.MaxChunkSize || !uint.IsPow2(entry.ChunkSize))
            {
                throw Invalid(
                    $"File entry {entry.Id} declares a chunk size of {entry.ChunkSize}; it must be a power of two between " +
                    $"{VaultLimits.MinChunkSize} and {VaultLimits.MaxChunkSize}.");
            }

            if (entry.DataOffset < 0)
            {
                throw Invalid($"File entry {entry.Id} declares a negative data offset ({entry.DataOffset}).");
            }

            long chunkCount = Math.Max(1, ((entry.Length + entry.ChunkSize) - 1) / entry.ChunkSize);
            if (chunkCount > VaultLimits.MaxChunkCount)
            {
                throw Invalid($"File entry {entry.Id} would need {chunkCount} chunks; at most {VaultLimits.MaxChunkCount} are allowed.");
            }

            blobs.Add((entry.DataOffset, entry.Length + (VaultLimits.TagSize * chunkCount)));
            return new WireEntry(entry, name, comment);
        }
    }

    /// <summary>Encodes a string as strict UTF-8, reporting unencodable input as an invalid index.</summary>
    /// <param name="value">Text to encode.</param>
    /// <param name="id">Id of the entry, for the error message.</param>
    /// <param name="field">Field name, for the error message.</param>
    private static byte[] Encode(string value, uint id, string field)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new VaultFormatException(
                VaultErrorCode.IndexInvalid,
                $"The {field} of entry {id} cannot be encoded as UTF-8.",
                ex);
        }
    }

    /// <summary>Decodes strict UTF-8, reporting an invalid sequence as an invalid index.</summary>
    /// <param name="bytes">Bytes to decode.</param>
    /// <param name="ordinal">Position of the entry, for the error message.</param>
    /// <param name="field">Field name, for the error message.</param>
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes, uint ordinal, string field)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new VaultFormatException(
                VaultErrorCode.IndexInvalid,
                $"The {field} of entry {ordinal} is not valid UTF-8.",
                ex);
        }
    }

    /// <summary>
    /// Returns the first character a comment may not contain, or <see langword="null"/>. Comments are
    /// rendered verbatim in the Properties dialog, so they get the same filter as names (FORMAT.md
    /// section 6.1): C0/C1 controls other than TAB, LF and CR, and every Cf/Zl/Zp formatting character.
    /// </summary>
    /// <param name="comment">Comment text to scan.</param>
    private static char? FindControlCharacter(string comment)
    {
        foreach (char c in comment)
        {
            if (c is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (EntryNames.IsControl(c) || EntryNames.IsBidiOrBom(c))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Clamps a tick count outside the <see cref="DateTime"/> range to 0 (section 4.4).</summary>
    /// <param name="ticks">Raw tick count from the file.</param>
    private static long ClampTicks(long ticks) => ticks is < 0 or > MaxTicks ? 0 : ticks;

    /// <summary>Creates the exception every index rule violation reports.</summary>
    /// <param name="message">Human-readable message; never contains key material.</param>
    private static VaultFormatException Invalid(string message) => new(VaultErrorCode.IndexInvalid, message);

    /// <summary>Writes a little-endian <see cref="ushort"/> and advances.</summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="position">Write position, advanced by two.</param>
    /// <param name="value">Value to write.</param>
    private static void WriteUInt16(Span<byte> buffer, ref int position, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[position..], value);
        position += sizeof(ushort);
    }

    /// <summary>Writes a little-endian <see cref="uint"/> and advances.</summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="position">Write position, advanced by four.</param>
    /// <param name="value">Value to write.</param>
    private static void WriteUInt32(Span<byte> buffer, ref int position, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[position..], value);
        position += sizeof(uint);
    }

    /// <summary>Writes a little-endian <see cref="ulong"/> and advances.</summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="position">Write position, advanced by eight.</param>
    /// <param name="value">Value to write.</param>
    private static void WriteUInt64(Span<byte> buffer, ref int position, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[position..], value);
        position += sizeof(ulong);
    }

    /// <summary>Writes a little-endian <see cref="long"/> and advances.</summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="position">Write position, advanced by eight.</param>
    /// <param name="value">Value to write.</param>
    private static void WriteInt64(Span<byte> buffer, ref int position, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer[position..], value);
        position += sizeof(long);
    }

    /// <summary>An entry together with its encoded variable-length fields.</summary>
    /// <param name="Entry">The entry itself.</param>
    /// <param name="Name">UTF-8 name bytes.</param>
    /// <param name="Comment">UTF-8 comment bytes.</param>
    private readonly record struct WireEntry(IndexEntry Entry, byte[] Name, byte[] Comment);

    /// <summary>Sibling identity: the parent id plus the name compared case-insensitively.</summary>
    private sealed class SiblingNameComparer : IEqualityComparer<(uint ParentId, string Name)>
    {
        /// <summary>The single instance.</summary>
        public static readonly SiblingNameComparer Instance = new();

        /// <summary>Compares two sibling keys.</summary>
        /// <param name="x">First key.</param>
        /// <param name="y">Second key.</param>
        public bool Equals((uint ParentId, string Name) x, (uint ParentId, string Name) y) =>
            x.ParentId == y.ParentId && EntryNames.Comparer.Equals(x.Name, y.Name);

        /// <summary>Hashes a sibling key.</summary>
        /// <param name="obj">Key to hash.</param>
        public int GetHashCode((uint ParentId, string Name) obj) =>
            HashCode.Combine(obj.ParentId, EntryNames.Comparer.GetHashCode(obj.Name));
    }

    /// <summary>
    /// A bounds-checked forward cursor. Every read verifies that enough bytes remain before it slices,
    /// so a hostile length field can never make the reader look past the buffer.
    /// </summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        /// <summary>Creates a cursor at the start of a buffer.</summary>
        /// <param name="data">Buffer to read.</param>
        public Cursor(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        /// <summary>Bytes not yet consumed.</summary>
        public readonly int Remaining => _data.Length - _position;

        /// <summary>Skips a number of bytes that the caller has already verified to be present.</summary>
        /// <param name="count">Bytes to skip.</param>
        public void Skip(int count) => _position += count < 0 ? 0 : Math.Min(count, Remaining);

        /// <summary>Reads one byte.</summary>
        /// <param name="value">The byte read, or 0.</param>
        public bool TryReadByte(out byte value)
        {
            if (Remaining < sizeof(byte))
            {
                value = 0;
                return false;
            }

            value = _data[_position];
            _position += sizeof(byte);
            return true;
        }

        /// <summary>Reads a little-endian <see cref="ushort"/>.</summary>
        /// <param name="value">The value read, or 0.</param>
        public bool TryReadUInt16(out ushort value)
        {
            if (Remaining < sizeof(ushort))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
            _position += sizeof(ushort);
            return true;
        }

        /// <summary>Reads a little-endian <see cref="uint"/>.</summary>
        /// <param name="value">The value read, or 0.</param>
        public bool TryReadUInt32(out uint value)
        {
            if (Remaining < sizeof(uint))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
            _position += sizeof(uint);
            return true;
        }

        /// <summary>Reads a little-endian <see cref="ulong"/>.</summary>
        /// <param name="value">The value read, or 0.</param>
        public bool TryReadUInt64(out ulong value)
        {
            if (Remaining < sizeof(ulong))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]);
            _position += sizeof(ulong);
            return true;
        }

        /// <summary>Reads a little-endian <see cref="long"/>.</summary>
        /// <param name="value">The value read, or 0.</param>
        public bool TryReadInt64(out long value)
        {
            if (Remaining < sizeof(long))
            {
                value = 0;
                return false;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(_data[_position..]);
            _position += sizeof(long);
            return true;
        }

        /// <summary>Reads a run of bytes.</summary>
        /// <param name="count">Number of bytes to read.</param>
        /// <param name="value">The bytes read, or an empty span.</param>
        public bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
        {
            if (count < 0 || Remaining < count)
            {
                value = default;
                return false;
            }

            value = _data.Slice(_position, count);
            _position += count;
            return true;
        }
    }
}
