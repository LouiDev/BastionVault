using System.Security.Cryptography;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The golden fixtures of FORMAT.md section 10. Every run rebuilds both vaults from the pinned seams
/// and compares them byte for byte with the files checked into <c>tests/fixtures</c>. A difference is
/// either a format change or a determinism bug in the writer; in both cases the fixtures are only
/// refreshed deliberately, by setting <c>BASTION_REGEN_GOLDEN=1</c>.
/// </summary>
public sealed class GoldenFixtureTests
{
    [Fact]
    public async Task The_empty_fixture_is_reproduced_byte_for_byte()
    {
        using var work = new TempDirectory("golden-empty");

        byte[] rebuilt = await GoldenVault.BuildEmptyAsync(work.Path);

        AssertMatchesFixture(GoldenVault.EmptyFixtureName, rebuilt);
    }

    [Fact]
    public async Task The_small_fixture_is_reproduced_byte_for_byte()
    {
        using var work = new TempDirectory("golden-small");

        byte[] rebuilt = await GoldenVault.BuildSmallAsync(work.Path);

        AssertMatchesFixture(GoldenVault.SmallFixtureName, rebuilt);
    }

    [Fact]
    public async Task Building_the_same_fixture_twice_in_one_process_gives_the_same_bytes()
    {
        using var first = new TempDirectory("golden-twice-a");
        using var second = new TempDirectory("golden-twice-b");

        byte[] one = await GoldenVault.BuildSmallAsync(first.Path);
        byte[] two = await GoldenVault.BuildSmallAsync(second.Path);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(one)), Convert.ToHexString(SHA256.HashData(two)));
        Assert.Equal(one.Length, two.Length);
    }

    [Fact]
    public async Task The_empty_fixture_opens_as_an_empty_vault()
    {
        using var work = new TempDirectory("golden-open-empty");
        string path = await CopyFixtureAsync(GoldenVault.EmptyFixtureName, work);

        await using IVaultSession session = await OpenAsync(path);

        Assert.Empty(session.GetChildren(EntryId.Root));
        Assert.False(session.IsDirty);
        Assert.False(session.IsReadOnly);
        Assert.Equal(GoldenVault.Kdf, session.Kdf);

        VaultStatistics statistics = session.Statistics;
        Assert.Equal(0, statistics.FolderCount);
        Assert.Equal(0, statistics.FileCount);
        Assert.Equal(0, statistics.TotalPlaintextBytes);
        Assert.Equal(1ul, statistics.SaveCounter);
        Assert.Equal(GoldenVault.Epoch, statistics.LastSavedUtc);
        Assert.False(statistics.OpenedFromIndexCopy);
        Assert.Equal(new FileInfo(path).Length, statistics.OnDiskBytes);

        // Header, both index copies and nothing else: the smallest legal vault.
        Assert.Equal(VaultHeader.Size + (2 * VaultLimits.MinIndexLength), new FileInfo(path).Length);
    }

    [Fact]
    public async Task The_small_fixture_opens_with_the_documented_tree_and_contents()
    {
        using var work = new TempDirectory("golden-open-small");
        string path = await CopyFixtureAsync(GoldenVault.SmallFixtureName, work);

        await using IVaultSession session = await OpenAsync(path);

        Assert.True(session.TryResolvePath(@"\Documents\2026\a.txt", out EntryId text));
        Assert.True(session.TryResolvePath(@"\empty.bin", out EntryId empty));
        Assert.True(session.TryResolvePath(@"\big.bin", out EntryId big));

        EntryInfo textInfo = session.Find(text)!;
        Assert.Equal("a.txt", textInfo.Name);
        Assert.Equal(3, textInfo.Length);
        Assert.Equal(GoldenVault.Comment, textInfo.Comment);
        Assert.Equal(EntryState.Stored, textInfo.State);
        Assert.Equal(GoldenVault.Epoch, textInfo.CreatedUtc);
        Assert.Equal(GoldenVault.Epoch, textInfo.ModifiedUtc);

        Assert.Equal(0, session.Find(empty)!.Length);
        Assert.Equal(GoldenVault.BigLength, session.Find(big)!.Length);

        Assert.Equal(GoldenVault.SmallText, System.Text.Encoding.UTF8.GetString(await ReadAllAsync(session, text)));
        Assert.Empty(await ReadAllAsync(session, empty));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(GoldenVault.BigContent())),
            Convert.ToHexString(SHA256.HashData(await ReadAllAsync(session, big))));

        // \Documents, \Documents\2026, \empty.bin, \big.bin at the top two levels.
        IReadOnlyList<EntryInfo> top = session.GetChildren(EntryId.Root);
        Assert.Equal(["Documents", "big.bin", "empty.bin"], top.Select(entry => entry.Name).ToArray());

        VaultStatistics statistics = session.Statistics;
        Assert.Equal(2, statistics.FolderCount);
        Assert.Equal(3, statistics.FileCount);
        Assert.Equal(GoldenVault.BigLength + 3, statistics.TotalPlaintextBytes);
        Assert.Equal(2ul, statistics.SaveCounter);
        Assert.Equal(GoldenVault.Epoch, statistics.LastSavedUtc);
        Assert.False(statistics.OpenedFromIndexCopy);
        Assert.Equal(new FileInfo(path).Length, statistics.OnDiskBytes);

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
        Assert.Equal(3, report.FilesChecked);
    }

    [Fact]
    public async Task The_small_fixture_exports_back_to_identical_files()
    {
        using var work = new TempDirectory("golden-export");
        string path = await CopyFixtureAsync(GoldenVault.SmallFixtureName, work);
        string destination = work.SubDirectory("out");

        await using IVaultSession session = await OpenAsync(path);
        ExportResult result = await session.ExportAsync(
            [.. session.GetChildren(EntryId.Root).Select(entry => entry.Id)],
            destination,
            new ExportOptions(),
            null,
            CancellationToken.None);

        Assert.Empty(result.Issues);
        Assert.Equal(3, result.FilesWritten);
        Assert.Equal(GoldenVault.SmallText, await File.ReadAllTextAsync(Path.Combine(destination, @"Documents\2026\a.txt")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(destination, "empty.bin")));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(GoldenVault.BigContent())),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(destination, "big.bin")))));
    }

    [Fact]
    public async Task The_small_fixture_lays_its_blobs_out_exactly_as_the_format_prescribes()
    {
        using var work = new TempDirectory("golden-layout");
        string path = await CopyFixtureAsync(GoldenVault.SmallFixtureName, work);

        using VaultImage image = VaultImage.Load(path, GoldenVault.Password);

        // Canonical order (section 4.5): depth-first pre-order, children by ascending id.
        Assert.Equal(
            ["Documents", "2026", "a.txt", "empty.bin", "big.bin"],
            image.Index.Entries.Select(entry => entry.Name).ToArray());

        // Blobs tile the data section exactly (rule 9) in canonical order.
        long expected = 0;
        foreach (IndexEntry entry in image.Index.Entries.Where(entry => entry.Kind == EntryKind.File))
        {
            Assert.Equal(expected, entry.DataOffset);
            Assert.Equal(VaultLimits.DefaultChunkSize, entry.ChunkSize);
            expected += BastionVault.Core.Crypto.ChunkCipher.BlobLength(entry.Length, entry.ChunkSize);
        }

        Assert.Equal(0, image.Index.DataPaddingLength);
        Assert.Equal(expected, image.Index.DataSectionLength);
        Assert.Equal(
            VaultHeader.Size + (2 * image.Header.IndexLength) + image.Index.DataSectionLength,
            image.Bytes.LongLength);
        Assert.Equal(VaultLimits.MinIndexLength, image.Header.IndexLength);

        // 2 MiB + 17 bytes at a 1 MiB chunk size is three chunks, the last one 17 bytes long.
        Assert.Equal(3u, BastionVault.Core.Crypto.ChunkCipher.ChunkCount(GoldenVault.BigLength, VaultLimits.DefaultChunkSize));
    }

    /// <summary>Opens a fixture with the golden password.</summary>
    /// <param name="path">Path of the copied fixture.</param>
    internal static async Task<IVaultSession> OpenAsync(string path)
    {
        using Passphrase password = Passphrase.FromString(GoldenVault.Password);
        return await new VaultFactory(new DeterministicRandomSource(7), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Copies a fixture into a scratch directory so a test can damage it freely.</summary>
    /// <param name="name">File name of the fixture.</param>
    /// <param name="work">Scratch directory.</param>
    internal static async Task<string> CopyFixtureAsync(string name, TempDirectory work)
    {
        string source = FixtureFiles.Path(name);
        Assert.True(
            File.Exists(source),
            $"The golden fixture {source} is missing. Run the suite once with BASTION_REGEN_GOLDEN=1 to create it.");

        string destination = work.File(name);
        await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(source));
        return destination;
    }

    /// <summary>Reads a whole entry through the decrypting stream.</summary>
    /// <param name="session">Session to read from.</param>
    /// <param name="id">File entry to read.</param>
    internal static async Task<byte[]> ReadAllAsync(IVaultSession session, EntryId id)
    {
        await using Stream stream = await session.OpenReadAsync(id, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Compares freshly built bytes with the checked-in fixture, or overwrites the fixture when
    /// <c>BASTION_REGEN_GOLDEN=1</c> is set.
    /// </summary>
    /// <param name="name">File name of the fixture.</param>
    /// <param name="rebuilt">The bytes this run produced.</param>
    private static void AssertMatchesFixture(string name, byte[] rebuilt)
    {
        string path = FixtureFiles.Path(name);

        if (FixtureFiles.RegenerationRequested)
        {
            Directory.CreateDirectory(FixtureFiles.Directory);
            File.WriteAllBytes(path, rebuilt);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"The golden fixture {path} is missing. Run the suite once with BASTION_REGEN_GOLDEN=1 to create it.");

        byte[] stored = File.ReadAllBytes(path);
        Assert.True(
            stored.Length == rebuilt.Length,
            $"{name}: the fixture is {stored.Length} bytes, the rebuild is {rebuilt.Length}.");

        int difference = FirstDifference(stored, rebuilt);
        Assert.True(
            difference < 0,
            $"{name}: the rebuild differs from the fixture at offset {difference} " +
            $"(fixture 0x{(difference < 0 ? 0 : stored[difference]):X2}, rebuild 0x{(difference < 0 ? 0 : rebuilt[difference]):X2}). " +
            "Set BASTION_REGEN_GOLDEN=1 only when the format really changed.");
    }

    /// <summary>Index of the first differing byte, or -1 when the two blocks are equal.</summary>
    /// <param name="left">First block.</param>
    /// <param name="right">Second block, of the same length.</param>
    private static int FirstDifference(byte[] left, byte[] right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return i;
            }
        }

        return -1;
    }
}
