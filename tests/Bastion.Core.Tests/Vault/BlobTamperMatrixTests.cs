using System.Security.Cryptography;
using Bastion.Core.Format;

namespace Bastion.Core.Tests.Vault;

/// <summary>
/// The data-section half of the tamper matrix of FORMAT.md section 10. Every case damages one blob and
/// then asks the three readers that exist for content the same question: <c>Verify</c> must report it,
/// <c>Export</c> must refuse it and leave nothing behind, and the decrypting stream must throw
/// <see cref="VaultErrorCode.DataCorrupt"/> at exactly the chunk that was touched.
/// </summary>
public sealed class BlobTamperMatrixTests
{
    /// <summary>Every chunk of the multi-chunk file, once in its ciphertext and once in its tag.</summary>
    public static TheoryData<uint, bool> ChunkTargets => new()
    {
        { 0u, false }, { 0u, true },
        { 1u, false }, { 1u, true },
        { 2u, false }, { 2u, true },
    };

    [Theory]
    [MemberData(nameof(ChunkTargets))]
    public async Task A_flipped_bit_in_a_chunk_is_caught_by_every_reader(uint chunk, bool inTag)
    {
        using TamperVault vault = await TamperVault.CreateAsync();

        byte[] mutated = vault.Copy();
        long offset;
        using (VaultImage image = vault.Image())
        {
            (long start, long length) = image.ChunkRange(TamperVault.BigFile, chunk);
            offset = inTag ? start + length - 1 : start;
        }

        mutated[offset] ^= 0x40;
        vault.Write(mutated);

        string because = $"chunk {chunk} of {TamperVault.BigFile} was damaged in its {(inTag ? "tag" : "ciphertext")}";
        await using IVaultSession session = await vault.OpenTargetAsync();
        Assert.True(session.TryResolvePath("\\" + TamperVault.BigFile, out EntryId big), because);

        // 1. Verify names the file and the chunk, and says the layout itself is still sound.
        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.False(report.IsClean, because);
        Assert.True(report.LayoutOk, $"{because}: the blobs still tile the data section");
        VerifyFailure failure = Assert.Single(report.Failures);
        Assert.Equal(big, failure.Id);
        Assert.Equal("\\" + TamperVault.BigFile, failure.VaultPath);
        Assert.Equal(chunk, failure.ChunkIndex);

        // 2. Export refuses the file and deletes the partial output.
        ExportResult export = await session.ExportAsync(
            [big], vault.ExportDirectory, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(0, export.FilesWritten);
        Assert.Equal(0, export.BytesWritten);
        ExportIssue issue = Assert.Single(export.Issues);
        Assert.Equal(ExportIssueKind.IntegrityFailure, issue.Kind);
        Assert.Equal(chunk, issue.ChunkIndex);
        Assert.Equal("\\" + TamperVault.BigFile, issue.VaultPath);
        vault.AssertNoPlaintextEscaped();

        // 3. The stream hands out the authenticated prefix and then stops at the damaged chunk.
        VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(
            () => ReadToEndAsync(session, big));

        VaultAssert.Failure(error, VaultErrorCode.DataCorrupt, because);
        Assert.Equal(chunk, error.ChunkIndex);
        Assert.Equal("\\" + TamperVault.BigFile, error.VaultPath);

        // 4. The neighbouring files are untouched.
        Assert.True(session.TryResolvePath("\\" + TamperVault.MarkerFile, out EntryId marker));
        Assert.Equal(
            TamperVault.Marker,
            System.Text.Encoding.ASCII.GetString(await GoldenFixtureTests.ReadAllAsync(session, marker)));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    public async Task Two_chunks_of_one_blob_that_change_places_are_refused(uint first, uint second)
    {
        using TamperVault vault = await TamperVault.CreateAsync();

        byte[] mutated = vault.Copy();
        using (VaultImage image = vault.Image())
        {
            (long a, long length) = image.ChunkRange(TamperVault.BigFile, first);
            (long b, long other) = image.ChunkRange(TamperVault.BigFile, second);
            Assert.Equal(length, other);

            byte[] buffer = mutated.AsSpan((int)a, (int)length).ToArray();
            mutated.AsSpan((int)b, (int)length).CopyTo(mutated.AsSpan((int)a));
            buffer.CopyTo(mutated.AsSpan((int)b));
        }

        vault.Write(mutated);

        await using IVaultSession session = await vault.OpenTargetAsync();
        Assert.True(session.TryResolvePath("\\" + TamperVault.BigFile, out EntryId big));

        VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(
            () => ReadToEndAsync(session, big));

        // The chunk index is part of every chunk AAD, so the first of the two swapped chunks fails.
        VaultAssert.Failure(error, VaultErrorCode.DataCorrupt, "two chunks of one blob changed places");
        Assert.Equal(Math.Min(first, second), error.ChunkIndex);

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.False(report.IsClean);
        Assert.True(report.LayoutOk);
    }

    [Fact]
    public async Task Two_blobs_of_the_same_length_that_change_places_are_refused()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        using (VaultImage image = vault.Image())
        {
            (long a, long length) = image.BlobRange(TamperVault.TwinA);
            (long b, long other) = image.BlobRange(TamperVault.TwinB);
            Assert.Equal(length, other);

            byte[] buffer = mutated.AsSpan((int)a, (int)length).ToArray();
            mutated.AsSpan((int)b, (int)length).CopyTo(mutated.AsSpan((int)a));
            buffer.CopyTo(mutated.AsSpan((int)b));
        }

        vault.Write(mutated);

        await using IVaultSession session = await vault.OpenTargetAsync();
        Assert.True(session.TryResolvePath("\\" + TamperVault.TwinA, out EntryId twinA));
        Assert.True(session.TryResolvePath("\\" + TamperVault.TwinB, out EntryId twinB));

        // Each blob has its own key, so neither of the exchanged blobs authenticates in its new place.
        foreach (EntryId id in new[] { twinA, twinB })
        {
            VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(
                () => ReadToEndAsync(session, id));
            VaultAssert.Failure(error, VaultErrorCode.DataCorrupt, "two blobs changed places");
            Assert.Equal(0u, error.ChunkIndex);
        }

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.LayoutOk, "swapping two equally long blobs does not disturb the tiling");
        Assert.Equal(2, report.Failures.Count);

        ExportResult export = await session.ExportAsync(
            [twinA, twinB], vault.ExportDirectory, new ExportOptions(), null, CancellationToken.None);
        Assert.Equal(0, export.FilesWritten);
        Assert.Equal(2, export.Issues.Count);
        vault.AssertNoPlaintextEscaped();
    }

    [Fact]
    public async Task A_blob_spliced_in_from_another_vault_is_refused()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false, seed: 11);

        // Same password, same cost parameters, same file contents: only the vault key differs, which is
        // exactly the situation of an attacker who owns a second vault of the same shape.
        using TamperVault donor = await TamperVault.CreateAsync(withBigFile: false, seed: 12);

        byte[] mutated = vault.Copy();
        using (VaultImage target = vault.Image())
        using (VaultImage source = donor.Image())
        {
            (long targetOffset, long length) = target.BlobRange(TamperVault.TwinA);
            (long sourceOffset, long sourceLength) = source.BlobRange(TamperVault.TwinA);
            Assert.Equal(length, sourceLength);
            Assert.False(
                mutated.AsSpan((int)targetOffset, (int)length)
                    .SequenceEqual(source.Bytes.AsSpan((int)sourceOffset, (int)length)),
                "two vaults with different keys must not produce the same ciphertext");

            source.Bytes.AsSpan((int)sourceOffset, (int)length).CopyTo(mutated.AsSpan((int)targetOffset));
        }

        vault.Write(mutated);

        await using IVaultSession session = await vault.OpenTargetAsync();
        Assert.True(session.TryResolvePath("\\" + TamperVault.TwinA, out EntryId twinA));

        VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(
            () => ReadToEndAsync(session, twinA));

        VaultAssert.Failure(error, VaultErrorCode.DataCorrupt, "a blob was spliced in from another vault");
        Assert.Equal("\\" + TamperVault.TwinA, error.VaultPath);
        vault.AssertNoPlaintextEscaped();
    }

    [Fact]
    public async Task A_blob_that_authenticates_but_carries_other_bytes_fails_the_commitment_hash()
    {
        // The blob hash of FORMAT.md section 2.8 is the last line of defence: it is what makes a clean
        // Verify mean "every byte is accounted for" even when the AEADs are all satisfied.
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);

        byte[] mutated = vault.Copy();
        using (VaultImage image = vault.Image())
        {
            IndexEntry entry = image.FileEntry(TamperVault.TwinA);
            byte[] hash = (byte[])entry.BlobHash!.Clone();
            hash[0] ^= 0x40;
            entry.BlobHash = hash;

            byte[] plaintext = IndexSerializer.Serialize(image.Index);
            mutated = image.BuildAroundIndex(
                plaintext,
                image.Bytes.AsSpan((int)image.DataSectionOffset, (int)image.Index.DataSectionLength).ToArray(),
                image.Index.DataSectionLength);
        }

        vault.Write(mutated);

        await using IVaultSession session = await vault.OpenTargetAsync();
        Assert.True(session.TryResolvePath("\\" + TamperVault.TwinA, out EntryId twinA));

        // Every chunk still authenticates, so the content reads back fine.
        Assert.Equal("aaa", System.Text.Encoding.ASCII.GetString(await GoldenFixtureTests.ReadAllAsync(session, twinA)));

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.False(report.IsClean, "a wrong commitment hash must not verify clean");
        VerifyFailure failure = Assert.Single(report.Failures);
        Assert.Equal(twinA, failure.Id);
        Assert.Null(failure.ChunkIndex);
    }

    [Fact]
    public async Task A_healthy_vault_of_the_same_shape_verifies_clean()
    {
        // The control case for the whole class: without tampering, all of the above is quiet.
        using TamperVault vault = await TamperVault.CreateAsync();
        vault.Write(vault.Copy());

        await using IVaultSession session = await vault.OpenTargetAsync();

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
        Assert.Equal(4, report.FilesChecked);

        Assert.True(session.TryResolvePath("\\" + TamperVault.BigFile, out EntryId big));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(TamperVault.BigContent())),
            Convert.ToHexString(SHA256.HashData(await GoldenFixtureTests.ReadAllAsync(session, big))));
    }

    /// <summary>Reads an entry to the end through the decrypting stream and discards the plaintext.</summary>
    /// <param name="session">Session holding the entry.</param>
    /// <param name="id">The file entry.</param>
    /// <returns>The number of plaintext bytes the stream produced before it stopped.</returns>
    private static async Task<long> ReadToEndAsync(IVaultSession session, EntryId id)
    {
        await using Stream stream = await session.OpenReadAsync(id, CancellationToken.None);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        return total;
    }
}
