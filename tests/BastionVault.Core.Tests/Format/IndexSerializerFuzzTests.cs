using System.Buffers.Binary;
using System.Diagnostics;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Format;

/// <summary>
/// FORMAT.md section 4.6 and the threat model's A4: a crafted index must never make the reader do
/// anything but return a valid tree or report <see cref="VaultErrorCode.IndexInvalid"/>.
/// </summary>
public sealed class IndexSerializerFuzzTests
{
    /// <summary>Number of mutated buffers the fuzz test feeds to the reader.</summary>
    private const int Iterations = 5000;

    /// <summary>Upper bound for the whole fuzz run; a pathological input would blow through it.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [Fact]
    public void Deserialize_OfAMutatedIndex_EitherSucceedsOrReportsIndexInvalid()
    {
        byte[] valid = IndexSerializer.Serialize(Sample());
        int meaningful = (int)BinaryPrimitives.ReadUInt32LittleEndian(valid.AsSpan(4));
        var random = new Random(0x8A5710);
        var clock = Stopwatch.StartNew();
        int accepted = 0;
        int rejected = 0;

        for (int i = 0; i < Iterations; i++)
        {
            byte[] mutated = Mutate(valid, meaningful, random);
            try
            {
                VaultIndex parsed = IndexSerializer.Deserialize(mutated);
                Assert.True(parsed.Entries.Count <= 1_000_000);
                accepted++;
            }
            catch (VaultFormatException error) when (error.Code == VaultErrorCode.IndexInvalid)
            {
                rejected++;
            }
            catch (Exception unexpected)
            {
                Assert.Fail($"Iteration {i} threw {unexpected.GetType().FullName}: {unexpected.Message}");
            }
        }

        clock.Stop();
        Assert.Equal(Iterations, accepted + rejected);
        Assert.True(rejected > Iterations / 10, $"Only {rejected} of {Iterations} mutations were rejected.");
        Assert.True(clock.Elapsed < Budget, $"The fuzz run took {clock.Elapsed}, the budget is {Budget}.");
    }

    [Fact]
    public void Deserialize_OfRandomBytes_ReportsIndexInvalid()
    {
        var random = new Random(0x1234_5678);

        for (int i = 0; i < 500; i++)
        {
            byte[] garbage = new byte[random.Next(0, 4096)];
            random.NextBytes(garbage);

            try
            {
                IndexSerializer.Deserialize(garbage);
            }
            catch (VaultFormatException error) when (error.Code == VaultErrorCode.IndexInvalid)
            {
                continue;
            }
            catch (Exception unexpected)
            {
                Assert.Fail($"Iteration {i} threw {unexpected.GetType().FullName}: {unexpected.Message}");
            }
        }
    }

    [Fact]
    public void Deserialize_OfAnEmptyBuffer_ReportsIndexInvalid()
    {
        VaultFormatException error = Assert.Throws<VaultFormatException>(() => IndexSerializer.Deserialize([]));

        Assert.Equal(VaultErrorCode.IndexInvalid, error.Code);
    }

    /// <summary>Applies one random bit flip, truncation or insertion to a copy of the buffer.</summary>
    /// <param name="source">The valid buffer to damage.</param>
    /// <param name="meaningful">Length of the part before the zero padding, where most mutations land.</param>
    /// <param name="random">Deterministic source of randomness.</param>
    private static byte[] Mutate(byte[] source, int meaningful, Random random)
    {
        switch (random.Next(3))
        {
            case 0:
            {
                byte[] copy = (byte[])source.Clone();
                int flips = random.Next(1, 9);
                for (int i = 0; i < flips; i++)
                {
                    copy[Position(source.Length, meaningful, random)] ^= (byte)(1 << random.Next(8));
                }

                return copy;
            }

            case 1:
            {
                int length = random.Next(4) == 0 ? random.Next(0, source.Length) : random.Next(0, meaningful + 8);
                return source[..length];
            }

            default:
            {
                int at = Position(source.Length + 1, meaningful, random);
                int count = random.Next(1, 17);
                byte[] inserted = new byte[count];
                random.NextBytes(inserted);
                return [.. source[..at], .. inserted, .. source[at..]];
            }
        }
    }

    /// <summary>
    /// Picks a byte position, four times out of five inside the meaningful head so the entry parser is
    /// exercised rather than the zero padding that dominates the buffer.
    /// </summary>
    /// <param name="length">Exclusive upper bound.</param>
    /// <param name="meaningful">Length of the meaningful head.</param>
    /// <param name="random">Deterministic source of randomness.</param>
    private static int Position(int length, int meaningful, Random random) =>
        random.Next(5) == 0 ? random.Next(length) : random.Next(Math.Min(meaningful, length));

    /// <summary>A small but structurally complete index: folders, files, comments and data padding.</summary>
    private static VaultIndex Sample()
    {
        var index = new VaultIndex
        {
            SaveCounter = 7,
            SavedUtcTicks = 638_000_000_000_000_000L,
            DataPaddingLength = 1024,
            NextEntryId = 50,
        };

        index.Entries.Add(new IndexEntry { Kind = EntryKind.Folder, Id = 1, ParentId = 0, Name = "Docs", Comment = "a comment" });
        index.Entries.Add(new IndexEntry { Kind = EntryKind.Folder, Id = 2, ParentId = 1, Name = "2026" });
        index.Entries.Add(Blob(3, 1, "notes.txt", 0, 100, 1));
        index.Entries.Add(Blob(4, 2, "photo.jpg", 116, 70000, 2));
        index.Entries.Add(Blob(5, 0, "empty.bin", 70148, 0, 3));

        index.DataSectionLength = 70164 + index.DataPaddingLength;
        return index;
    }

    /// <summary>Builds a file entry with a 64 KiB chunk size.</summary>
    /// <param name="id">Entry id.</param>
    /// <param name="parentId">Parent id.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="offset">Blob offset.</param>
    /// <param name="length">Plaintext length.</param>
    /// <param name="seed">Byte the blob id and hash are filled with.</param>
    private static IndexEntry Blob(uint id, uint parentId, string name, long offset, long length, byte seed) => new()
    {
        Kind = EntryKind.File,
        Id = id,
        ParentId = parentId,
        Name = name,
        BlobId = IndexPlaintextBuilder.Fill(16, seed),
        BlobHash = IndexPlaintextBuilder.Fill(32, seed),
        DataOffset = offset,
        Length = length,
        ChunkSize = 65536,
    };
}
