namespace Bastion.Core.Tests.Vault;

/// <summary>
/// The freshness row of the tamper matrix of FORMAT.md section 10: a blob taken from an earlier save of
/// the same vault, put back after its content changed. Every content write gets a fresh blob id
/// (section 2.7), so the old ciphertext cannot authenticate in the new file even though both were
/// written with the same vault key, the same password and the same header.
/// </summary>
public sealed class ReplayTamperTests
{
    /// <summary>Password of every vault in this class.</summary>
    private const string Password = TamperVault.Password;

    /// <summary>Name of the file whose content is replaced between the two saves.</summary>
    private const string FileName = "payload.bin";

    /// <summary>Plaintext length of that file; both versions are the same size so the splice fits.</summary>
    private const int Length = 4096;

    [Fact]
    public async Task A_blob_replayed_from_an_earlier_save_of_the_same_vault_is_refused()
    {
        using var work = new TempDirectory("replay");
        (byte[] first, byte[] second, string path) = await BuildTwoGenerationsAsync(work);

        byte[] mutated = (byte[])second.Clone();
        using (VaultImage older = VaultImage.Load(WriteSnapshot(work, "v1.bastion", first), Password))
        using (VaultImage newer = VaultImage.Load(WriteSnapshot(work, "v2.bastion", second), Password))
        {
            (long oldOffset, long oldLength) = older.BlobRange(FileName);
            (long newOffset, long newLength) = newer.BlobRange(FileName);
            Assert.Equal(oldLength, newLength);
            Assert.False(
                older.FileEntry(FileName).BlobId!.AsSpan().SequenceEqual(newer.FileEntry(FileName).BlobId),
                "every content write must use a fresh blob id");

            older.Bytes.AsSpan((int)oldOffset, (int)oldLength).CopyTo(mutated.AsSpan((int)newOffset));
        }

        string tampered = WriteSnapshot(work, "tampered.bastion", mutated);

        await using IVaultSession session = await OpenAsync(tampered);
        Assert.True(session.TryResolvePath("\\" + FileName, out EntryId file));

        VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(async () =>
        {
            await using Stream stream = await session.OpenReadAsync(file, CancellationToken.None);
            using var sink = new MemoryStream();
            await stream.CopyToAsync(sink);
        });

        VaultAssert.Failure(error, VaultErrorCode.DataCorrupt, "a blob was replayed from the previous save");
        Assert.Equal(0u, error.ChunkIndex);

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.False(report.IsClean, "a replayed blob must not verify clean");
        Assert.True(report.LayoutOk, "the replayed blob has the same length, so the tiling still holds");
        Assert.Equal(file, Assert.Single(report.Failures).Id);

        string destination = work.SubDirectory("export");
        ExportResult export = await session.ExportAsync(
            [file], destination, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(0, export.FilesWritten);
        Assert.Equal(ExportIssueKind.IntegrityFailure, Assert.Single(export.Issues).Kind);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public async Task A_whole_file_rollback_is_only_visible_in_the_save_counter()
    {
        // THREAT-MODEL.md A2 is explicit that a complete, valid older vault cannot be detected from the
        // inside. This test pins the one signal that does survive, so that removing it would fail here.
        using var work = new TempDirectory("rollback");
        (byte[] first, byte[] second, _) = await BuildTwoGenerationsAsync(work);

        await using (IVaultSession newer = await OpenAsync(WriteSnapshot(work, "newer.bastion", second)))
        {
            Assert.Equal(3ul, newer.Statistics.SaveCounter);
        }

        await using IVaultSession older = await OpenAsync(WriteSnapshot(work, "older.bastion", first));

        Assert.Equal(2ul, older.Statistics.SaveCounter);
        Assert.True((await older.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    /// <summary>
    /// Writes one vault twice: the first save stores <see cref="FileName"/> with one content, the second
    /// stores a different content of the same length under the same name.
    /// </summary>
    /// <param name="work">Scratch directory.</param>
    /// <returns>The bytes of both saves and the path they were written to.</returns>
    private static async Task<(byte[] First, byte[] Second, string Path)> BuildTwoGenerationsAsync(TempDirectory work)
    {
        string sources = work.SubDirectory("source");
        string before = Path.Combine(sources, FileName);
        await File.WriteAllBytesAsync(before, Content(1));

        string path = Path.Combine(work.Path, "vault.bastion");
        var options = new ImportOptions(PreserveTimestamps: false);

        using Passphrase password = Passphrase.FromString(Password);
        var factory = new VaultFactory(new DeterministicRandomSource(21), new FixedClock(GoldenVault.Epoch));

        await using IVaultSession session = await factory.CreateAsync(
            path, password, null, GoldenVault.Kdf, null, CancellationToken.None);

        await session.ImportAsync(EntryId.Root, [before], options, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        byte[] first = await File.ReadAllBytesAsync(path);

        Assert.True(session.TryResolvePath("\\" + FileName, out EntryId old));
        await session.DeleteAsync([old], CancellationToken.None);

        string after = Path.Combine(work.SubDirectory("source2"), FileName);
        await File.WriteAllBytesAsync(after, Content(2));
        await session.ImportAsync(EntryId.Root, [after], options, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        byte[] second = await File.ReadAllBytesAsync(path);

        Assert.Equal(first.Length, second.Length);
        return (first, second, path);
    }

    /// <summary>Deterministic content of one generation of the file.</summary>
    /// <param name="generation">Generation number; different generations differ in every byte.</param>
    private static byte[] Content(int generation)
    {
        byte[] content = new byte[Length];
        new DeterministicRandomSource((ulong)generation).Fill(content);
        return content;
    }

    /// <summary>Writes a vault image into the scratch directory and returns its path.</summary>
    /// <param name="work">Scratch directory.</param>
    /// <param name="name">File name to write.</param>
    /// <param name="bytes">The image.</param>
    private static string WriteSnapshot(TempDirectory work, string name, byte[] bytes)
    {
        string path = work.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Opens a vault of this class with its password.</summary>
    /// <param name="path">Path of the vault.</param>
    private static async Task<IVaultSession> OpenAsync(string path)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await new VaultFactory(new DeterministicRandomSource(77), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
