namespace Bastion.Core.Tests.Session;

/// <summary>Verification, damaged vaults, recovery and size obfuscation.</summary>
public sealed class IntegrityTests
{
    /// <summary>2.5 MiB gives three chunks, so a middle chunk can be damaged while the first still reads.</summary>
    private const int MultiChunkLength = (5 * 1024 * 1024) / 2;

    [Fact]
    public async Task A_healthy_vault_verifies_clean()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.ImportAsync(
            EntryId.Root,
            [
                context.WriteSourceFile("a.bin", VaultTestContext.Bytes(1000, 61)),
                context.WriteSourceFile("b.bin", VaultTestContext.Bytes(MultiChunkLength, 62)),
            ],
            new ImportOptions(),
            null,
            CancellationToken.None);

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
        Assert.True(report.LayoutOk);
        Assert.Equal(2, report.FilesChecked);
        Assert.Empty(report.Failures);
        Assert.True(report.BytesChecked > MultiChunkLength);
    }

    [Fact]
    public async Task A_damaged_chunk_is_reported_by_verify_and_refused_by_export()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(50_000, 63);

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.ImportAsync(
                EntryId.Root, [context.WriteSourceFile("fragile.bin", content)], new ImportOptions(), null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        long dataStart = await DataSectionOffsetAsync(context);
        VaultTestContext.FlipByte(context.VaultPath, dataStart + 128);

        await using IVaultSession reopened = await context.OpenAsync();

        VerifyReport report = await reopened.VerifyAsync(null, CancellationToken.None);
        Assert.False(report.IsClean);
        Assert.True(report.LayoutOk);
        VerifyFailure failure = Assert.Single(report.Failures);
        Assert.Equal("\\fragile.bin", failure.VaultPath);
        Assert.Equal(0u, failure.ChunkIndex);

        string exportDirectory = Path.Combine(context.Root, "export");
        ExportResult export = await reopened.ExportAsync(
            [VaultTestContext.Entry(reopened, "fragile.bin").Id], exportDirectory, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(0, export.FilesWritten);
        Assert.Contains(export.Issues, issue => issue.Kind == ExportIssueKind.IntegrityFailure);
        Assert.Empty(Directory.GetFiles(exportDirectory));
    }

    [Fact]
    public async Task Reading_a_damaged_chunk_through_the_stream_reports_data_corruption()
    {
        using var context = new VaultTestContext();

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.ImportAsync(
                EntryId.Root,
                [context.WriteSourceFile("fragile.bin", VaultTestContext.Bytes(9000, 64))],
                new ImportOptions(),
                null,
                CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        long dataStart = await DataSectionOffsetAsync(context);
        VaultTestContext.FlipByte(context.VaultPath, dataStart + 64);

        await using IVaultSession reopened = await context.OpenAsync();
        EntryId id = VaultTestContext.Entry(reopened, "fragile.bin").Id;

        VaultIntegrityException error = await Assert.ThrowsAsync<VaultIntegrityException>(
            () => VaultTestContext.ReadAllAsync(reopened, id));

        Assert.Equal(VaultErrorCode.DataCorrupt, error.Code);
        Assert.Equal("\\fragile.bin", error.VaultPath);
        Assert.Equal(0u, error.ChunkIndex);
    }

    [Fact]
    public async Task Recover_writes_the_authenticated_prefix_of_a_damaged_file()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(MultiChunkLength, 65);

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.ImportAsync(
                EntryId.Root, [context.WriteSourceFile("partial.bin", content)], new ImportOptions(), null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        // Damage the second chunk so the first megabyte still authenticates.
        long dataStart = await DataSectionOffsetAsync(context);
        VaultTestContext.FlipByte(context.VaultPath, dataStart + (1024 * 1024) + 16 + 32);

        await using IVaultSession reopened = await context.OpenAsync();
        string exportDirectory = Path.Combine(context.Root, "recover");

        ExportResult recovered = await reopened.RecoverAsync(
            exportDirectory, new ExportOptions(WritePartialFiles: true), null, CancellationToken.None);

        Assert.Equal(0, recovered.FilesWritten);
        Assert.Contains(recovered.Issues, issue => issue.Kind == ExportIssueKind.PartialWritten);

        string partial = Path.Combine(exportDirectory, "partial.bin.partial");
        Assert.True(File.Exists(partial));

        byte[] prefix = File.ReadAllBytes(partial);
        Assert.Equal(1024 * 1024, prefix.Length);
        Assert.Equal(VaultTestContext.Digest(content[..(1024 * 1024)]), VaultTestContext.Digest(prefix));
    }

    [Fact]
    public async Task A_damaged_primary_index_falls_back_to_the_copy()
    {
        using var context = new VaultTestContext();

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
            await session.ImportAsync(
                EntryId.Root,
                [context.WriteSourceFile("kept.bin", VaultTestContext.Bytes(2048, 66))],
                new ImportOptions(),
                null,
                CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        VaultTestContext.FlipByte(context.VaultPath, 160 + 32);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.True(reopened.Statistics.OpenedFromIndexCopy);
        Assert.Equal(2, reopened.GetChildren(EntryId.Root).Count);
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);

        // Saving repairs the vault: both index copies are written again.
        await reopened.CreateFolderAsync(EntryId.Root, "Repaired", CancellationToken.None);
        await reopened.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.False(reopened.Statistics.OpenedFromIndexCopy);

        await using IVaultSession again = await context.OpenAsync();
        Assert.False(again.Statistics.OpenedFromIndexCopy);
        Assert.Equal(3, again.GetChildren(EntryId.Root).Count);
    }

    [Fact]
    public async Task Both_indexes_damaged_reports_a_corrupt_index()
    {
        using var context = new VaultTestContext();

        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        long length = new FileInfo(context.VaultPath).Length;
        VaultTestContext.FlipByte(context.VaultPath, 160 + 32);
        VaultTestContext.FlipByte(context.VaultPath, length - 64);

        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        Assert.Equal(VaultErrorCode.IndexCorrupt, error.Code);
    }

    [Fact]
    public async Task Truncating_a_vault_is_reported()
    {
        using var context = new VaultTestContext();

        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        long length = new FileInfo(context.VaultPath).Length;
        using (FileStream stream = new(context.VaultPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(length - 1024);
        }

        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        Assert.Equal(VaultErrorCode.Truncated, error.Code);
    }

    [Fact]
    public async Task Size_obfuscation_pads_the_data_section_and_still_verifies()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(300_000, 67);

        long plain;
        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.ImportAsync(
                EntryId.Root, [context.WriteSourceFile("padded.bin", content)], new ImportOptions(), null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            plain = new FileInfo(context.VaultPath).Length;

            await session.CreateFolderAsync(EntryId.Root, "Trigger", CancellationToken.None);
            await session.SaveAsync(new SaveOptions(SizeObfuscation: true), null, CancellationToken.None);
        }

        long padded = new FileInfo(context.VaultPath).Length;
        Assert.True(padded > plain, $"expected the padded vault ({padded}) to be larger than the plain one ({plain})");

        await using IVaultSession reopened = await context.OpenAsync();
        VerifyReport report = await reopened.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
        Assert.Equal(
            VaultTestContext.Digest(content),
            VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(reopened, VaultTestContext.Entry(reopened, "padded.bin").Id)));
    }

    [Fact]
    public async Task Every_save_writes_fresh_index_nonces()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        byte[] first = ReadHeader(context.VaultPath);
        await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        byte[] second = ReadHeader(context.VaultPath);

        Assert.NotEqual(first.AsSpan(124, 12).ToArray(), second.AsSpan(124, 12).ToArray());
        Assert.NotEqual(first.AsSpan(136, 12).ToArray(), second.AsSpan(136, 12).ToArray());
        Assert.NotEqual(second.AsSpan(124, 12).ToArray(), second.AsSpan(136, 12).ToArray());

        // An ordinary save keeps the wrapped key, its salt and its nonce byte for byte.
        Assert.Equal(first.AsSpan(32, 32).ToArray(), second.AsSpan(32, 32).ToArray());
        Assert.Equal(first.AsSpan(64, 60).ToArray(), second.AsSpan(64, 60).ToArray());
    }

    /// <summary>The absolute offset of the data section of the vault under test.</summary>
    /// <param name="context">The test context.</param>
    private static async Task<long> DataSectionOffsetAsync(VaultTestContext context)
    {
        VaultHeaderInfo info = await context.NewFactory().ReadHeaderAsync(context.VaultPath, CancellationToken.None);
        return 160 + info.IndexLength;
    }

    /// <summary>Reads the 160 header bytes of a vault.</summary>
    /// <param name="path">Path of the vault.</param>
    private static byte[] ReadHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[160];
        stream.ReadExactly(header);
        return header;
    }
}
