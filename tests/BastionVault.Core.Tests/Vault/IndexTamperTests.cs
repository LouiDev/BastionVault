using BastionVault.Core.Format;
using BastionVault.Core.Tests.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The index half of the tamper matrix of FORMAT.md section 10: a damaged primary index, a damaged
/// copy, both damaged, an index that belongs to an earlier save, and indices that are crafted to
/// violate exactly one rule of section 4.6 while being encrypted with the real index key of the vault.
/// </summary>
public sealed class IndexTamperTests
{
    /// <summary>Offsets inside the encrypted index that the matrix flips: body, boundary and tag.</summary>
    public static TheoryData<int> IndexOffsets =>
        [0, 1, 4096, (int)VaultLimits.MinIndexLength - 17, (int)VaultLimits.MinIndexLength - 1];

    /// <summary>The single rule of FORMAT.md section 4.6 that a crafted index breaks.</summary>
    public enum CraftedFlaw
    {
        /// <summary>Rule 3: two entries share an id.</summary>
        DuplicateIds,

        /// <summary>Rule 7: two file entries share a blob id.</summary>
        DuplicateBlobIds,

        /// <summary>Rule 4: a child is serialized before the folder it belongs to.</summary>
        ParentLaterThanChild,

        /// <summary>Rule 4: the parent id belongs to a file entry.</summary>
        ParentIsAFile,

        /// <summary>Rule 9: the blobs leave a gap in the data section.</summary>
        NonTilingOffsets,

        /// <summary>Rule 9: two blobs claim the same bytes.</summary>
        OverlappingOffsets,

        /// <summary>Rule 6: the name is longer than 765 bytes.</summary>
        OversizeName,

        /// <summary>Section 4.4: the comment is longer than 4096 bytes.</summary>
        OversizeComment,

        /// <summary>Rule 2: more entries than the format allows.</summary>
        OversizeEntryCount,

        /// <summary>Rule 8: the chunk size is above 64 MiB.</summary>
        OversizeChunkSize,

        /// <summary>Rule 8: the plaintext length is above 2^48 - 1.</summary>
        OversizeFileLength,

        /// <summary>Rule 1: the padding after the entry array is not zero.</summary>
        NonZeroPadding,

        /// <summary>Rule 1: the index version is not 1.</summary>
        WrongIndexVersion,

        /// <summary>Rule 5: the tree is 129 levels deep.</summary>
        TooDeep,
    }

    [Theory]
    [MemberData(nameof(IndexOffsets))]
    public async Task A_damaged_primary_index_opens_from_the_copy_and_a_save_repairs_it(int offset)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        mutated[VaultHeader.Size + offset] ^= 0x40;
        vault.Write(mutated);

        // The primary index no longer authenticates at all.
        Assert.Throws<VaultFormatException>(() => VaultImage.Load(vault.TargetPath, TamperVault.Password));

        await using (IVaultSession damaged = await vault.OpenTargetAsync())
        {
            Assert.True(damaged.Statistics.OpenedFromIndexCopy, "the vault must report that it used the index copy");
            Assert.Equal(3, damaged.GetChildren(EntryId.Root).Count);
            Assert.True(damaged.TryResolvePath("\\" + TamperVault.MarkerFile, out EntryId marker));
            Assert.Equal(
                TamperVault.Marker,
                System.Text.Encoding.ASCII.GetString(await GoldenFixtureTests.ReadAllAsync(damaged, marker)));

            // FORMAT.md section 4.1: "save to repair".
            await damaged.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        using (VaultImage repaired = VaultImage.Load(vault.TargetPath, TamperVault.Password))
        {
            Assert.Equal(3, repaired.Index.Entries.Count);
        }

        await using IVaultSession reopened = await vault.OpenTargetAsync();
        Assert.False(reopened.Statistics.OpenedFromIndexCopy, "the repairing save must restore the primary index");
    }

    [Theory]
    [MemberData(nameof(IndexOffsets))]
    public async Task A_damaged_index_copy_alone_goes_unnoticed(int offset)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        mutated[mutated.Length - (int)VaultLimits.MinIndexLength + offset] ^= 0x40;
        vault.Write(mutated);

        await using IVaultSession session = await vault.OpenTargetAsync();

        Assert.False(session.Statistics.OpenedFromIndexCopy);
        Assert.Equal(3, session.GetChildren(EntryId.Root).Count);
    }

    [Theory]
    [MemberData(nameof(IndexOffsets))]
    public async Task Both_indexes_damaged_report_a_corrupt_index(int offset)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        mutated[VaultHeader.Size + offset] ^= 0x40;
        mutated[mutated.Length - (int)VaultLimits.MinIndexLength + offset] ^= 0x40;

        string because = $"both index copies were damaged at offset {offset}";
        VaultException error = await vault.ExpectOpenFailsAsync(mutated, because);

        VaultAssert.Failure(error, VaultErrorCode.IndexCorrupt, because);
    }

    [Fact]
    public async Task Swapping_the_primary_index_with_its_copy_is_refused()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        // Both ciphertexts carry the same plaintext, but each one is bound to its own nonce.
        byte[] mutated = vault.Copy();
        int length = (int)VaultLimits.MinIndexLength;
        Array.Copy(mutated, mutated.Length - length, mutated, VaultHeader.Size, length);
        Array.Copy(vault.Original, VaultHeader.Size, mutated, mutated.Length - length, length);

        VaultException error = await vault.ExpectOpenFailsAsync(mutated, "the two index copies were exchanged");

        VaultAssert.Failure(error, VaultErrorCode.IndexCorrupt, "exchanged index copies");
    }

    [Fact]
    public async Task An_index_from_an_earlier_save_pasted_onto_the_newer_header_is_refused()
    {
        using var work = new TempDirectory("index-rollback");
        string path = Path.Combine(work.Path, "rollback.bastion");
        string sourceFile = Path.Combine(work.SubDirectory("source"), "note.txt");
        await File.WriteAllBytesAsync(sourceFile, "first"u8.ToArray());

        byte[] first;
        byte[] second;
        using (Passphrase password = Passphrase.FromString(TamperVault.Password))
        {
            var factory = new VaultFactory(new DeterministicRandomSource(3), new FixedClock(GoldenVault.Epoch));
            await using IVaultSession session = await factory.CreateAsync(
                path, password, null, GoldenVault.Kdf, null, CancellationToken.None);

            await session.ImportAsync(
                EntryId.Root, [sourceFile], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            first = await File.ReadAllBytesAsync(path);

            await session.CreateFolderAsync(EntryId.Root, "Added later", CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            second = await File.ReadAllBytesAsync(path);
        }

        Assert.Equal(first.Length, second.Length);

        // The wrapped key and the salt survive an ordinary save, so only the fresh index nonces stand
        // between an attacker and a silent rollback of the tree.
        int length = (int)VaultLimits.MinIndexLength;
        byte[] mutated = (byte[])second.Clone();
        Array.Copy(first, VaultHeader.Size, mutated, VaultHeader.Size, length);
        Array.Copy(first, first.Length - length, mutated, mutated.Length - length, length);

        string tampered = Path.Combine(work.Path, "tampered.bastion");
        await File.WriteAllBytesAsync(tampered, mutated);

        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(async () =>
        {
            using Passphrase password = Passphrase.FromString(TamperVault.Password);
            await using IVaultSession session = await new VaultFactory(
                    new DeterministicRandomSource(9), new FixedClock(GoldenVault.Epoch))
                .OpenAsync(tampered, password, null, OpenOptions.Default, null, CancellationToken.None);
        });

        VaultAssert.Failure(error, VaultErrorCode.IndexCorrupt, "an index from the previous save");
    }

    [Theory]
    [InlineData(CraftedFlaw.DuplicateIds)]
    [InlineData(CraftedFlaw.DuplicateBlobIds)]
    [InlineData(CraftedFlaw.ParentLaterThanChild)]
    [InlineData(CraftedFlaw.ParentIsAFile)]
    [InlineData(CraftedFlaw.NonTilingOffsets)]
    [InlineData(CraftedFlaw.OverlappingOffsets)]
    [InlineData(CraftedFlaw.OversizeName)]
    [InlineData(CraftedFlaw.OversizeComment)]
    [InlineData(CraftedFlaw.OversizeEntryCount)]
    [InlineData(CraftedFlaw.OversizeChunkSize)]
    [InlineData(CraftedFlaw.OversizeFileLength)]
    [InlineData(CraftedFlaw.NonZeroPadding)]
    [InlineData(CraftedFlaw.WrongIndexVersion)]
    [InlineData(CraftedFlaw.TooDeep)]
    public async Task A_crafted_index_encrypted_with_the_real_key_is_refused_as_invalid(CraftedFlaw flaw)
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] file;
        using (VaultImage image = vault.Image())
        {
            IndexPlaintextBuilder builder = Craft(flaw);
            file = image.BuildAroundIndex(builder.Build(), null, (long)builder.DataSectionLength);
        }

        string because = $"the index violates {flaw}";
        VaultException error = await vault.ExpectOpenFailsAsync(file, because);

        VaultAssert.Failure(error, VaultErrorCode.IndexInvalid, because);
    }

    [Fact]
    public async Task A_crafted_index_that_breaks_no_rule_still_opens()
    {
        // The counterpart of the matrix above: the crafting machinery itself produces a vault the
        // reader accepts, so every rejection above really is caused by the injected flaw.
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] file;
        using (VaultImage image = vault.Image())
        {
            var builder = new IndexPlaintextBuilder { NextEntryId = 3, DataSectionLength = 0 };
            builder.AddFolder(1, 0, "Documents");
            builder.AddFolder(2, 1, "2026");
            file = image.BuildAroundIndex(builder.Build(), null, 0);
        }

        vault.Write(file);
        await using IVaultSession session = await vault.OpenTargetAsync();

        Assert.True(session.TryResolvePath(@"\Documents\2026", out EntryId year));
        Assert.Equal("2026", session.Find(year)!.Name);
        Assert.Equal(0, session.Statistics.FileCount);
    }

    /// <summary>Builds an index plaintext that breaks exactly one rule of FORMAT.md section 4.6.</summary>
    /// <param name="flaw">The rule to break.</param>
    private static IndexPlaintextBuilder Craft(CraftedFlaw flaw)
    {
        const uint chunkSize = 65536;
        ulong blob = IndexPlaintextBuilder.BlobLength(100, chunkSize);

        var builder = new IndexPlaintextBuilder { NextEntryId = 100 };
        switch (flaw)
        {
            case CraftedFlaw.DuplicateIds:
                builder.DataSectionLength = 0;
                builder.AddFolder(1, 0, "one");
                builder.AddFolder(1, 0, "two");
                break;

            case CraftedFlaw.DuplicateBlobIds:
                builder.DataSectionLength = 2 * blob;
                builder.AddFile(1, 0, "a.bin", 0, 100, chunkSize, blobSeed: 7);
                builder.AddFile(2, 0, "b.bin", blob, 100, chunkSize, blobSeed: 7);
                break;

            case CraftedFlaw.ParentLaterThanChild:
                builder.DataSectionLength = 0;
                builder.AddFolder(2, 1, "child");
                builder.AddFolder(1, 0, "parent");
                break;

            case CraftedFlaw.ParentIsAFile:
                builder.DataSectionLength = blob;
                builder.AddFile(1, 0, "a.bin", 0, 100, chunkSize);
                builder.AddFolder(2, 1, "inside a file");
                break;

            case CraftedFlaw.NonTilingOffsets:
                builder.DataSectionLength = (2 * blob) + 32;
                builder.AddFile(1, 0, "a.bin", 0, 100, chunkSize, blobSeed: 1);
                builder.AddFile(2, 0, "b.bin", blob + 32, 100, chunkSize, blobSeed: 2);
                break;

            case CraftedFlaw.OverlappingOffsets:
                builder.DataSectionLength = 2 * blob;
                builder.AddFile(1, 0, "a.bin", 0, 100, chunkSize, blobSeed: 1);
                builder.AddFile(2, 0, "b.bin", blob - 16, 100, chunkSize, blobSeed: 2);
                break;

            case CraftedFlaw.OversizeName:
                builder.DataSectionLength = 0;
                builder.AddFolder(1, 0, new string('n', 766));
                break;

            case CraftedFlaw.OversizeComment:
                builder.DataSectionLength = 0;
                builder.AddFolder(1, 0, "commented", new string('c', VaultLimits.MaxCommentBytes + 1));
                break;

            case CraftedFlaw.OversizeEntryCount:
                builder.DataSectionLength = 0;
                builder.AddFolder(1, 0, "one");
                builder.EntryCountOverride = VaultLimits.MaxEntries + 1;
                break;

            case CraftedFlaw.OversizeChunkSize:
                builder.DataSectionLength = 116;
                builder.AddFile(1, 0, "a.bin", 0, 100, VaultLimits.MaxChunkSize * 2);
                break;

            case CraftedFlaw.OversizeFileLength:
                builder.DataSectionLength = ulong.MaxValue;
                builder.AddFile(1, 0, "a.bin", 0, 1UL << 48, VaultLimits.MaxChunkSize);
                break;

            case CraftedFlaw.NonZeroPadding:
                builder.DataSectionLength = 0;
                builder.AddFolder(1, 0, "one");
                builder.PaddingByte = 0xEE;
                break;

            case CraftedFlaw.WrongIndexVersion:
                builder.DataSectionLength = 0;
                builder.IndexVersion = 2;
                builder.AddFolder(1, 0, "one");
                break;

            default:
                builder.DataSectionLength = 0;
                builder.NextEntryId = 200;
                for (uint level = 1; level <= VaultLimits.MaxDepth + 1; level++)
                {
                    builder.AddFolder(level, level - 1, $"level{level}");
                }

                break;
        }

        return builder;
    }
}
