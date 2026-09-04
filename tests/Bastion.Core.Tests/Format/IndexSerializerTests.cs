using System.Buffers.Binary;
using System.Text;
using Bastion.Core.Format;

namespace Bastion.Core.Tests.Format;

/// <summary>Covers FORMAT.md sections 4.3 to 4.6: layout, canonical order and every validity rule.</summary>
public sealed class IndexSerializerTests
{
    /// <summary>Chunk size every fixture uses (64 KiB, the smallest legal one).</summary>
    private const uint ChunkSize = 65536;

    // ─────────────────────────── round trip and layout ───────────────────────────

    [Fact]
    public void EmptyIndex_RoundTrips()
    {
        var index = new VaultIndex { SaveCounter = 1, NextEntryId = 1 };

        byte[] bytes = IndexSerializer.Serialize(index);
        VaultIndex parsed = IndexSerializer.Deserialize(bytes);

        Assert.Equal(65536, bytes.Length);
        Assert.Empty(parsed.Entries);
        Assert.Equal(1u, parsed.SaveCounter);
        Assert.Equal(0, parsed.DataSectionLength);
    }

    [Fact]
    public void NestedTreeWithCommentsAndUnicodeNames_RoundTrips()
    {
        VaultIndex index = Sample();

        byte[] bytes = IndexSerializer.Serialize(index);
        VaultIndex parsed = IndexSerializer.Deserialize(bytes);

        Assert.Equal(index.SaveCounter, parsed.SaveCounter);
        Assert.Equal(index.SavedUtcTicks, parsed.SavedUtcTicks);
        Assert.Equal(index.DataSectionLength, parsed.DataSectionLength);
        Assert.Equal(index.DataPaddingLength, parsed.DataPaddingLength);
        Assert.Equal(index.NextEntryId, parsed.NextEntryId);
        Assert.Equal(index.Entries.Count, parsed.Entries.Count);

        foreach (IndexEntry expected in index.Entries)
        {
            IndexEntry actual = Assert.Single(parsed.Entries, e => e.Id == expected.Id);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.ParentId, actual.ParentId);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Comment, actual.Comment);
            Assert.Equal(expected.CreatedUtcTicks, actual.CreatedUtcTicks);
            Assert.Equal(expected.ModifiedUtcTicks, actual.ModifiedUtcTicks);
            Assert.Equal(expected.Attributes, actual.Attributes);
            Assert.Equal(expected.BlobId, actual.BlobId);
            Assert.Equal(expected.BlobHash, actual.BlobHash);
            Assert.Equal(expected.DataOffset, actual.DataOffset);
            Assert.Equal(expected.Length, actual.Length);
            Assert.Equal(expected.ChunkSize, actual.ChunkSize);
        }

        // Re-serializing the parsed tree must reproduce the very same bytes.
        Assert.Equal(bytes, IndexSerializer.Serialize(parsed));
    }

    [Fact]
    public void Serialize_WritesTheDocumentedFixedBlockAndPadsWithZeroes()
    {
        VaultIndex index = Sample();

        byte[] bytes = IndexSerializer.Serialize(index);

        uint unpaddedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(index.SaveCounter, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(index.SavedUtcTicks, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)));
        Assert.Equal((ulong)index.DataSectionLength, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24)));
        Assert.Equal((ulong)index.DataPaddingLength, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(index.NextEntryId, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal((uint)index.Entries.Count, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44)));

        Assert.Equal(PadLadder.Index(unpaddedLength), bytes.Length);
        Assert.All(bytes[(int)unpaddedLength..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Serialize_IsIndependentOfTheInputOrder()
    {
        VaultIndex ordered = Sample();
        byte[] first = IndexSerializer.Serialize(ordered);
        byte[] second = IndexSerializer.Serialize(Sample());

        var shuffled = new VaultIndex
        {
            SaveCounter = ordered.SaveCounter,
            SavedUtcTicks = ordered.SavedUtcTicks,
            DataSectionLength = ordered.DataSectionLength,
            DataPaddingLength = ordered.DataPaddingLength,
            NextEntryId = ordered.NextEntryId,
            Entries = [.. Shuffle(Sample().Entries, seed: 20260902)],
        };
        byte[] third = IndexSerializer.Serialize(shuffled);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Fact]
    public void Serialize_OrdersDepthFirstPreOrderWithChildrenByAscendingId()
    {
        var index = new VaultIndex { NextEntryId = 8 };
        index.Entries.Add(Folder(5, 0, "b-root"));
        index.Entries.Add(Folder(2, 0, "a-root"));
        index.Entries.Add(Folder(7, 2, "child-7"));
        index.Entries.Add(Folder(3, 2, "child-3"));
        index.Entries.Add(Folder(6, 3, "grandchild"));
        index.Entries.Add(Folder(1, 5, "child-1"));

        VaultIndex parsed = IndexSerializer.Deserialize(IndexSerializer.Serialize(index));

        Assert.Equal([2u, 3u, 6u, 7u, 5u, 1u], parsed.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Deserialize_ClampsTimestampsOutsideTheDateTimeRange()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.SavedUtcTicks = long.MinValue;
        builder.Entries[0].CreatedUtcTicks = -1;
        builder.Entries[0].ModifiedUtcTicks = 3155378975999999999L + 1;
        builder.Entries[1].CreatedUtcTicks = 3155378975999999999L;

        VaultIndex parsed = IndexSerializer.Deserialize(builder.Build());

        Assert.Equal(0, parsed.SavedUtcTicks);
        Assert.Equal(0, parsed.Entries[0].CreatedUtcTicks);
        Assert.Equal(0, parsed.Entries[0].ModifiedUtcTicks);
        Assert.Equal(3155378975999999999L, parsed.Entries[1].CreatedUtcTicks);
    }

    [Fact]
    public void Deserialize_AcceptsTabNewlineAndCarriageReturnInComments()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Comment = Encoding.UTF8.GetBytes("first\tsecond\r\nthird");

        VaultIndex parsed = IndexSerializer.Deserialize(builder.Build());

        Assert.Equal("first\tsecond\r\nthird", parsed.Entries[0].Comment);
    }

    [Fact]
    public void Deserialize_AcceptsAFullyPaddedDataSection()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.DataSectionLength = 116 + 4096;
        builder.DataPaddingLength = 4096;

        VaultIndex parsed = IndexSerializer.Deserialize(builder.Build());

        Assert.Equal(116 + 4096, parsed.DataSectionLength);
        Assert.Equal(4096, parsed.DataPaddingLength);
    }

    [Fact]
    public void Deserialize_AcceptsDepth128()
    {
        Assert.Equal(128, IndexSerializer.Deserialize(NestedChain(128).Build()).Entries.Count);
    }

    // ─────────────────────────── section 4.6, rule by rule ───────────────────────────

    [Fact]
    public void Rule1_RejectsAnIndexVersionOtherThanOne()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.IndexVersion = 2;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule1_RejectsAnUnpaddedLengthBeyondTheBuffer()
    {
        byte[] bytes = IndexPlaintextBuilder.Valid().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length + 1);

        AssertInvalid(bytes);
    }

    [Fact]
    public void Rule1_RejectsAnUnpaddedLengthBelowTheFixedBlock()
    {
        byte[] bytes = IndexPlaintextBuilder.Valid().Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 47);

        AssertInvalid(bytes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Rule1_RejectsAnEntryArrayThatDoesNotEndAtUnpaddedLength(int delta)
    {
        byte[] bytes = IndexPlaintextBuilder.Valid().Build();
        uint actual = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(actual + delta));

        AssertInvalid(bytes);
    }

    [Fact]
    public void Rule1_RejectsANonZeroPaddingByte()
    {
        byte[] bytes = IndexPlaintextBuilder.Valid().Build();
        bytes[^1] = 1;

        AssertInvalid(bytes);
    }

    [Fact]
    public void Rule1_RejectsPaddingRightAfterTheEntryArray()
    {
        byte[] bytes = IndexPlaintextBuilder.Valid().Build();
        uint unpaddedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        bytes[(int)unpaddedLength] = 0x80;

        AssertInvalid(bytes);
    }

    [Fact]
    public void Rule1_RejectsAPlaintextShorterThanTheFixedBlock()
    {
        AssertInvalid(new byte[47]);
        AssertInvalid([]);
    }

    [Fact]
    public void Rule1_RejectsAPlaintextLargerThanTheFormatAllows()
    {
        AssertInvalid(new byte[(64 * 1024 * 1024) + 1]);
    }

    [Fact]
    public void Rule2_RejectsMoreThanAMillionEntries()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.EntryCountOverride = 1_000_001;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule2_RejectsAnEntryCountThatOutrunsTheData()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.EntryCountOverride = 999_999;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule3_RejectsADuplicateId()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3 };
        builder.AddFolder(1, 0, "one");
        builder.AddFolder(1, 0, "two");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule3_RejectsIdZero()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3 };
        builder.AddFolder(0, 0, "zero");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule3_RejectsTheAllOnesId()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = uint.MaxValue };
        builder.AddFolder(uint.MaxValue, 0, "sentinel");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule3_RejectsAnIdAtOrAboveNextEntryId()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 5 };
        builder.AddFolder(5, 0, "too-high");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule4_RejectsAParentThatAppearsLater()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3 };
        builder.AddFolder(1, 2, "child-first");
        builder.AddFolder(2, 0, "parent-second");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule4_RejectsAParentThatIsAFile()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 116 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100);
        builder.AddFolder(2, 1, "under-a-file");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule4_RejectsAParentThatDoesNotExist()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3 };
        builder.AddFolder(1, 99, "orphan");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule5_RejectsDepth129()
    {
        AssertInvalid(NestedChain(129).Build());
    }

    [Fact]
    public void Rule6_RejectsSiblingNamesThatDifferOnlyInCase()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3 };
        builder.AddFolder(1, 0, "Docs");
        builder.AddFolder(2, 0, "dOcS");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_AllowsTheSameNameUnderDifferentParents()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 4 };
        builder.AddFolder(1, 0, "Docs");
        builder.AddFolder(2, 0, "Other");
        builder.AddFolder(3, 1, "Other");

        Assert.Equal(3, IndexSerializer.Deserialize(builder.Build()).Entries.Count);
    }

    [Fact]
    public void Rule6_RejectsInvalidUtf8InAName()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = [0x41, 0xFF, 0xFE, 0x42];

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_RejectsAnOverlongUtf8Encoding()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = [0xC0, 0xAF];   // overlong "/"

        AssertInvalid(builder.Build());
    }

    [Theory]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad:name")]
    [InlineData("bad\"name")]
    [InlineData("bad|name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bell\u0007char")]
    [InlineData("c1\u0085")]
    [InlineData("rtl\u202Eoverride")]
    [InlineData("lrm\u200E")]
    [InlineData("bom\uFEFF")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT9.log")]
    [InlineData("NUL")]
    public void Rule6_RejectsEveryInvalidNameClass(string name)
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = Encoding.UTF8.GetBytes(name);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_RejectsAnEmptyName()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = [];

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_RejectsANameOf256CodeUnits()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = Encoding.UTF8.GetBytes(new string('a', 256));

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_RejectsANameLongerThan765Bytes()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Name = Encoding.UTF8.GetBytes(new string('\u00E9', 400));   // 800 bytes

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule6_RejectsANameLengthThatOutrunsTheBuffer()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[1].NameLengthOverride = 700;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule7_RejectsADuplicateBlobId()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 116 + 66 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, blobSeed: 7);
        builder.AddFile(2, 0, "b.txt", dataOffset: 116, length: 50, blobSeed: 7);

        AssertInvalid(builder.Build());
    }

    [Theory]
    [InlineData(65535u)]        // below the minimum and not a power of two
    [InlineData(32768u)]        // a power of two, below the minimum
    [InlineData(196608u)]       // inside the range, not a power of two
    [InlineData(134217728u)]    // a power of two, above the maximum
    [InlineData(0u)]
    public void Rule8_RejectsAnIllegalChunkSize(uint chunkSize)
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 2, DataSectionLength = 116 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, chunkSize: chunkSize);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule8_RejectsALengthAboveTwoToThe48()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 2 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 1UL << 48, chunkSize: 1 << 26);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule8_RejectsAChunkCountAboveTwoToThe32MinusOne()
    {
        // 2^48-1 bytes in 64 KiB chunks need 2^32 chunks, one more than the counter can hold.
        var builder = new IndexPlaintextBuilder { NextEntryId = 2 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: (1UL << 48) - 1, chunkSize: 65536);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule8_AcceptsTheLargestLegalChunkCount()
    {
        ulong length = (1UL << 48) - 65536;
        ulong blobLength = IndexPlaintextBuilder.BlobLength(length, 65536);
        var builder = new IndexPlaintextBuilder { NextEntryId = 2, DataSectionLength = blobLength };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: length, chunkSize: 65536);

        VaultIndex parsed = IndexSerializer.Deserialize(builder.Build());

        Assert.Equal((long)length, parsed.Entries[0].Length);
    }

    [Fact]
    public void Rule9_RejectsAFirstOffsetOtherThanZero()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 2, DataSectionLength = 16 + 116 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 16, length: 100);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_RejectsAGapBetweenBlobs()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 200 + 66 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, blobSeed: 1);
        builder.AddFile(2, 0, "b.txt", dataOffset: 200, length: 50, blobSeed: 2);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_RejectsOverlappingBlobs()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 116 + 66 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, blobSeed: 1);
        builder.AddFile(2, 0, "b.txt", dataOffset: 100, length: 50, blobSeed: 2);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_RejectsTwoBlobsAtTheSameOffset()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 116 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, blobSeed: 1);
        builder.AddFile(2, 0, "b.txt", dataOffset: 0, length: 100, blobSeed: 2);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_RejectsATrailingHoleThatIsNotDeclaredAsPadding()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 2, DataSectionLength = 117 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100);

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_RejectsContentWithoutAnyBlob()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 2, DataSectionLength = 4096 };
        builder.AddFolder(1, 0, "empty");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule9_AcceptsThreeBlobsThatTileExactly()
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = 4, DataSectionLength = 116 + 66 + 16 };
        builder.AddFile(1, 0, "a.txt", dataOffset: 0, length: 100, blobSeed: 1);
        builder.AddFile(2, 0, "b.txt", dataOffset: 116, length: 50, blobSeed: 2);
        builder.AddFile(3, 0, "empty.txt", dataOffset: 182, length: 0, blobSeed: 3);

        Assert.Equal(3, IndexSerializer.Deserialize(builder.Build()).Entries.Count);
    }

    [Fact]
    public void Rule10_RejectsMorePaddingThanDataSection()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.DataPaddingLength = builder.DataSectionLength + 1;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Rule10_RejectsADataSectionLengthThatCannotBeAFileOffset()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.DataSectionLength = ulong.MaxValue;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_RejectsAnUnknownKind()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Kind = 2;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_RejectsNonZeroAttributes()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Attributes = 1;

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_RejectsACommentLongerThan4096Bytes()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Comment = Encoding.UTF8.GetBytes(new string('x', 4097));

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_AcceptsACommentOfExactly4096Bytes()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Comment = Encoding.UTF8.GetBytes(new string('x', 4096));

        Assert.Equal(4096, IndexSerializer.Deserialize(builder.Build()).Entries[0].Comment.Length);
    }

    [Fact]
    public void Entry_RejectsAControlCharacterInAComment()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Comment = Encoding.UTF8.GetBytes("bell\u0007char");

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_RejectsInvalidUtf8InAComment()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[0].Comment = [0xE0, 0x80];

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Entry_RejectsATruncatedFileTail()
    {
        IndexPlaintextBuilder builder = IndexPlaintextBuilder.Valid();
        builder.Entries[1].WriteFileTail = false;

        AssertInvalid(builder.Build());
    }

    // ─────────────────────────── the writer refuses invalid trees ───────────────────────────

    [Fact]
    public void Serialize_RejectsACycle()
    {
        var index = new VaultIndex { NextEntryId = 3 };
        index.Entries.Add(Folder(1, 2, "a"));
        index.Entries.Add(Folder(2, 1, "b"));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsAnEntryWhoseParentIsAFile()
    {
        var index = new VaultIndex { NextEntryId = 3, DataSectionLength = 116 };
        index.Entries.Add(File(1, 0, "a.txt", offset: 0, length: 100, seed: 1));
        index.Entries.Add(Folder(2, 1, "under-a-file"));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsASiblingNameCollision()
    {
        var index = new VaultIndex { NextEntryId = 3 };
        index.Entries.Add(Folder(1, 0, "Docs"));
        index.Entries.Add(Folder(2, 0, "DOCS"));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsAnInvalidName()
    {
        var index = new VaultIndex { NextEntryId = 2 };
        index.Entries.Add(Folder(1, 0, "trailing."));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsAnUnpairedSurrogateInAName()
    {
        var index = new VaultIndex { NextEntryId = 2 };
        index.Entries.Add(Folder(1, 0, "lone\uD800end"));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsANonTilingDataSection()
    {
        var index = new VaultIndex { NextEntryId = 2, DataSectionLength = 999 };
        index.Entries.Add(File(1, 0, "a.txt", offset: 0, length: 100, seed: 1));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsADuplicateBlobId()
    {
        var index = new VaultIndex { NextEntryId = 3, DataSectionLength = 116 + 66 };
        index.Entries.Add(File(1, 0, "a.txt", offset: 0, length: 100, seed: 4));
        index.Entries.Add(File(2, 0, "b.txt", offset: 116, length: 50, seed: 4));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsAFileWithoutABlobId()
    {
        var index = new VaultIndex { NextEntryId = 2, DataSectionLength = 116 };
        IndexEntry entry = File(1, 0, "a.txt", offset: 0, length: 100, seed: 1);
        entry.BlobId = null;
        index.Entries.Add(entry);

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsAnIdAtOrAboveNextEntryId()
    {
        var index = new VaultIndex { NextEntryId = 1 };
        index.Entries.Add(Folder(1, 0, "a"));

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_RejectsDepth129()
    {
        var index = new VaultIndex { NextEntryId = 130 };
        for (uint i = 1; i <= 129; i++)
        {
            index.Entries.Add(Folder(i, i - 1, $"level{i}"));
        }

        AssertInvalid(() => IndexSerializer.Serialize(index));
    }

    [Fact]
    public void Serialize_AcceptsDepth128()
    {
        var index = new VaultIndex { NextEntryId = 129 };
        for (uint i = 1; i <= 128; i++)
        {
            index.Entries.Add(Folder(i, i - 1, $"level{i}"));
        }

        Assert.Equal(128, IndexSerializer.Deserialize(IndexSerializer.Serialize(index)).Entries.Count);
    }

    [Fact]
    public void Serialize_RejectsANullIndex()
    {
        Assert.Throws<ArgumentNullException>(() => IndexSerializer.Serialize(null!));
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Asserts that the buffer is rejected with <see cref="VaultErrorCode.IndexInvalid"/>.</summary>
    /// <param name="plaintext">Padded index plaintext.</param>
    private static void AssertInvalid(byte[] plaintext) => AssertInvalid(() => IndexSerializer.Deserialize(plaintext));

    /// <summary>Asserts that the action fails with <see cref="VaultErrorCode.IndexInvalid"/>.</summary>
    /// <param name="action">Action under test.</param>
    private static void AssertInvalid(Func<object?> action)
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => action());
        Assert.Equal(VaultErrorCode.IndexInvalid, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>A chain of nested folders of the requested depth.</summary>
    /// <param name="depth">Number of nested folders.</param>
    private static IndexPlaintextBuilder NestedChain(int depth)
    {
        var builder = new IndexPlaintextBuilder { NextEntryId = (uint)depth + 1 };
        for (uint i = 1; i <= depth; i++)
        {
            builder.AddFolder(i, i - 1, $"level{i}");
        }

        return builder;
    }

    /// <summary>A tree with folders, files, comments, unicode names and data padding.</summary>
    private static VaultIndex Sample()
    {
        var index = new VaultIndex
        {
            SaveCounter = 42,
            SavedUtcTicks = 638_000_000_000_000_000L,
            DataPaddingLength = 4096,
            NextEntryId = 100,
        };

        index.Entries.Add(Folder(10, 0, "Documents", "top level"));
        index.Entries.Add(Folder(11, 10, "2026"));
        index.Entries.Add(Folder(12, 10, "\u65E5\u672C\u8A9E"));
        index.Entries.Add(File(20, 11, "notes.txt", offset: 0, length: 100, seed: 1, comment: "first\nsecond\ttabbed"));
        index.Entries.Add(File(21, 11, "\u00DCn\u00EFc\u00F8d\u00E9.md", offset: 116, length: 0, seed: 2));
        index.Entries.Add(File(22, 12, "\uD83D\uDE00 emoji.bin", offset: 132, length: 70000, seed: 3, chunkSize: 65536));
        index.Entries.Add(File(23, 0, "root-file.dat", offset: 132 + 70032, length: 1, seed: 4));

        long content = 116 + 16 + 70032 + 17;
        index.DataSectionLength = content + index.DataPaddingLength;
        return index;
    }

    /// <summary>Builds a folder entry.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="parentId">Parent id.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="comment">Entry comment.</param>
    private static IndexEntry Folder(uint id, uint parentId, string name, string comment = "") => new()
    {
        Kind = EntryKind.Folder,
        Id = id,
        ParentId = parentId,
        Name = name,
        Comment = comment,
        CreatedUtcTicks = 637_000_000_000_000_000L,
        ModifiedUtcTicks = 637_100_000_000_000_000L,
    };

    /// <summary>Builds a file entry.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="parentId">Parent id.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="offset">Blob offset.</param>
    /// <param name="length">Plaintext length.</param>
    /// <param name="seed">Byte the blob id and hash are filled with.</param>
    /// <param name="comment">Entry comment.</param>
    /// <param name="chunkSize">Chunk size.</param>
    private static IndexEntry File(
        uint id,
        uint parentId,
        string name,
        long offset,
        long length,
        byte seed,
        string comment = "",
        uint chunkSize = ChunkSize) => new()
        {
            Kind = EntryKind.File,
            Id = id,
            ParentId = parentId,
            Name = name,
            Comment = comment,
            CreatedUtcTicks = 637_200_000_000_000_000L,
            ModifiedUtcTicks = 637_300_000_000_000_000L,
            BlobId = IndexPlaintextBuilder.Fill(16, seed),
            BlobHash = IndexPlaintextBuilder.Fill(32, seed),
            DataOffset = offset,
            Length = length,
            ChunkSize = chunkSize,
        };

    /// <summary>Returns the entries in a deterministic but different order.</summary>
    /// <param name="entries">Entries to shuffle.</param>
    /// <param name="seed">Seed of the shuffle.</param>
    private static List<IndexEntry> Shuffle(List<IndexEntry> entries, int seed)
    {
        var random = new Random(seed);
        var copy = new List<IndexEntry>(entries);
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }
}
