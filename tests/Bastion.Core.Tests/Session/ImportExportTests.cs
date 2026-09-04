using System.Diagnostics;

namespace Bastion.Core.Tests.Session;

/// <summary>Importing from disk, previewing, exporting back and the staging store behind it.</summary>
public sealed class ImportExportTests
{
    /// <summary>2.5 MiB crosses the 1 MiB chunk boundary twice, so the last-chunk flag really gets exercised.</summary>
    private const int MultiChunkLength = (5 * 1024 * 1024) / 2;

    [Fact]
    public async Task Files_of_every_shape_survive_import_save_reopen_and_export()
    {
        using var context = new VaultTestContext();
        byte[] empty = [];
        byte[] tiny = [1, 2, 3];
        byte[] large = VaultTestContext.Bytes(MultiChunkLength, 42);

        string emptyPath = context.WriteSourceFile("empty.bin", empty);
        string tinyPath = context.WriteSourceFile("tiny.bin", tiny);
        string largePath = context.WriteSourceFile("large.bin", large);

        await using (IVaultSession session = await context.CreateAsync())
        {
            ImportResult result = await session.ImportAsync(
                EntryId.Root, [emptyPath, tinyPath, largePath], new ImportOptions(), null, CancellationToken.None);

            Assert.Equal(3, result.Imported.Count);
            Assert.Empty(result.Issues);
            Assert.Equal((long)(empty.Length + tiny.Length + large.Length), result.BytesImported);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        string exportDirectory = Path.Combine(context.Root, "export");
        await using (IVaultSession reopened = await context.OpenAsync())
        {
            Assert.Equal(3, reopened.Statistics.FileCount);

            ExportResult export = await reopened.ExportAsync(
                reopened.GetChildren(EntryId.Root).Select(entry => entry.Id).ToList(),
                exportDirectory,
                new ExportOptions(),
                null,
                CancellationToken.None);

            Assert.Equal(3, export.FilesWritten);
            Assert.Empty(export.Issues);
            Assert.Equal((long)(empty.Length + tiny.Length + large.Length), export.BytesWritten);
        }

        Assert.Equal(empty, File.ReadAllBytes(Path.Combine(exportDirectory, "empty.bin")));
        Assert.Equal(tiny, File.ReadAllBytes(Path.Combine(exportDirectory, "tiny.bin")));
        Assert.Equal(VaultTestContext.Digest(large), VaultTestContext.Digest(File.ReadAllBytes(Path.Combine(exportDirectory, "large.bin"))));
    }

    [Fact]
    public async Task A_directory_tree_is_imported_with_its_shape()
    {
        using var context = new VaultTestContext();
        string tree = Path.Combine(context.SourceDirectory, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        File.WriteAllBytes(Path.Combine(tree, "top.bin"), VaultTestContext.Bytes(10, 1));
        File.WriteAllBytes(Path.Combine(tree, "sub", "deep.bin"), VaultTestContext.Bytes(20, 2));
        Directory.CreateDirectory(Path.Combine(tree, "empty"));

        await using IVaultSession session = await context.CreateAsync();
        ImportResult result = await session.ImportAsync(EntryId.Root, [tree], new ImportOptions(), null, CancellationToken.None);

        Assert.Equal(5, result.Imported.Count);
        Assert.True(session.TryResolvePath("\\tree\\sub\\deep.bin", out EntryId deep));
        Assert.Equal(20L, session.Find(deep)!.Length);
        Assert.True(session.TryResolvePath("\\tree\\empty", out EntryId emptyFolder));
        Assert.Equal(0, session.Find(emptyFolder)!.ChildCount);

        string exportDirectory = Path.Combine(context.Root, "export");
        ExportResult export = await session.ExportAsync(
            [session.GetChildren(EntryId.Root).Single().Id], exportDirectory, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(2, export.FilesWritten);
        Assert.Equal(3, export.FoldersCreated);
        Assert.True(Directory.Exists(Path.Combine(exportDirectory, "tree", "empty")));
        Assert.Equal(
            VaultTestContext.Digest(File.ReadAllBytes(Path.Combine(tree, "sub", "deep.bin"))),
            VaultTestContext.Digest(File.ReadAllBytes(Path.Combine(exportDirectory, "tree", "sub", "deep.bin"))));
    }

    [Fact]
    public async Task A_pending_entry_can_be_previewed_and_exported_before_it_is_saved()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(300_000, 9);
        string source = context.WriteSourceFile("pending.bin", content);

        await using IVaultSession session = await context.CreateAsync();
        ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
        EntryId id = imported.Imported.Single();

        Assert.Equal(content, await VaultTestContext.ReadAllAsync(session, id));

        string before = Path.Combine(context.Root, "before");
        await session.ExportAsync([id], before, new ExportOptions(), null, CancellationToken.None);

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        string after = Path.Combine(context.Root, "after");
        await session.ExportAsync([id], after, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(before, "pending.bin")));
        Assert.Equal(
            VaultTestContext.Digest(File.ReadAllBytes(Path.Combine(before, "pending.bin"))),
            VaultTestContext.Digest(File.ReadAllBytes(Path.Combine(after, "pending.bin"))));
    }

    [Fact]
    public async Task Staging_spills_into_a_container_that_disappears_after_the_save()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(MultiChunkLength, 13);
        string source = context.WriteSourceFile("spill.bin", content);

        await using (IVaultSession session = await context.CreateThenOpenAsync(new OpenOptions(InMemoryStagingLimit: 1024 * 1024)))
        {
            await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

            string[] containers = Directory.GetFiles(context.Root, "*~stage-*");
            Assert.Single(containers);
            Assert.True(new FileInfo(containers[0]).Length > 1024 * 1024);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            Assert.Empty(Directory.GetFiles(context.Root, "*~stage-*"));
        }

        await using IVaultSession reopened = await context.OpenAsync();
        EntryInfo entry = VaultTestContext.Entry(reopened, "spill.bin");
        Assert.Equal(VaultTestContext.Digest(content), VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(reopened, entry.Id)));
    }

    [Fact]
    public async Task Deleting_a_large_file_and_saving_shrinks_the_vault()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("big.bin", VaultTestContext.Bytes(MultiChunkLength, 17));

        await using IVaultSession session = await context.CreateAsync();
        ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        long withData = new FileInfo(context.VaultPath).Length;
        Assert.True(withData > MultiChunkLength);

        await session.DeleteAsync([imported.Imported.Single()], CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        long withoutData = new FileInfo(context.VaultPath).Length;
        Assert.True(withoutData < withData - MultiChunkLength + 4096);
        Assert.Equal(withoutData, session.Statistics.OnDiskBytes);
    }

    [Fact]
    public async Task Import_renames_a_disk_name_the_vault_cannot_store()
    {
        using var context = new VaultTestContext();
        string awkward = Path.Combine(context.SourceDirectory, "CON.txt ");
        try
        {
            File.WriteAllBytes(@"\\?\" + awkward, VaultTestContext.Bytes(16, 3));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The file system refused the reserved name; nothing to test on this machine.
            return;
        }

        await using IVaultSession session = await context.CreateAsync();
        ImportResult result = await session.ImportAsync(
            EntryId.Root, [@"\\?\" + awkward], new ImportOptions(), null, CancellationToken.None);

        Assert.Single(result.Imported);
        Assert.Contains(result.Issues, issue => issue.Kind == ImportIssueKind.Renamed);
        Assert.Equal("CON_.txt", session.Find(result.Imported[0])!.Name);
    }

    [Fact]
    public async Task Import_skips_a_junction_instead_of_following_it()
    {
        using var context = new VaultTestContext();
        string target = Path.Combine(context.SourceDirectory, "target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "inside.bin"), VaultTestContext.Bytes(8, 4));

        string tree = Path.Combine(context.SourceDirectory, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "real.bin"), VaultTestContext.Bytes(8, 5));

        string junction = Path.Combine(tree, "link");
        if (!TryCreateJunction(junction, target))
        {
            return;
        }

        await using IVaultSession session = await context.CreateAsync();
        ImportResult result = await session.ImportAsync(EntryId.Root, [tree], new ImportOptions(), null, CancellationToken.None);

        Assert.Contains(result.Issues, issue => issue.Kind == ImportIssueKind.SkippedReparsePoint);
        Assert.False(session.TryResolvePath("\\tree\\link", out _));
        Assert.True(session.TryResolvePath("\\tree\\real.bin", out _));
    }

    [Fact]
    public async Task Import_honours_the_conflict_policy()
    {
        using var context = new VaultTestContext();
        string first = context.WriteSourceFile("same.bin", VaultTestContext.Bytes(10, 6));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [first], new ImportOptions(), null, CancellationToken.None);

        await session.ImportAsync(EntryId.Root, [first], new ImportOptions(Conflict: ConflictPolicy.Rename), null, CancellationToken.None);
        Assert.Equal(2, session.GetChildren(EntryId.Root).Count);
        Assert.Contains(session.GetChildren(EntryId.Root), entry => entry.Name == "same (2).bin");

        ImportResult skipped = await session.ImportAsync(
            EntryId.Root, [first], new ImportOptions(Conflict: ConflictPolicy.Skip), null, CancellationToken.None);
        Assert.Empty(skipped.Imported);
        Assert.Equal(2, session.GetChildren(EntryId.Root).Count);

        ImportResult replaced = await session.ImportAsync(
            EntryId.Root, [first], new ImportOptions(Conflict: ConflictPolicy.Replace), null, CancellationToken.None);
        Assert.Single(replaced.Imported);
        Assert.Equal(2, session.GetChildren(EntryId.Root).Count);
    }

    [Fact]
    public async Task Cancelling_an_import_discards_its_staged_blobs_and_leaves_the_tree_alone()
    {
        using var context = new VaultTestContext();
        string clash = context.WriteSourceFile("b-clash.bin", VaultTestContext.Bytes(1000, 7));
        string fresh = context.WriteSourceFile("a-fresh.bin", VaultTestContext.Bytes(200_000, 8));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [clash], new ImportOptions(), null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        var inner = (global::Bastion.Core.Session.VaultSession)session;
        Assert.Equal(0L, inner.Staging.StagedBytes);

        var options = new ImportOptions(ConflictResolver: (_, _) => ValueTask.FromResult(ConflictDecision.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ImportAsync(EntryId.Root, [fresh, clash], options, null, CancellationToken.None));

        Assert.Single(session.GetChildren(EntryId.Root));
        Assert.Equal("b-clash.bin", session.GetChildren(EntryId.Root)[0].Name);
        Assert.Equal(0L, inner.Staging.StagedBytes);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Export_renames_around_an_existing_file_and_can_skip_it()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(64, 19);
        string source = context.WriteSourceFile("report.txt", content);

        await using IVaultSession session = await context.CreateAsync();
        ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
        EntryId id = imported.Imported.Single();

        string exportDirectory = Path.Combine(context.Root, "export");
        Directory.CreateDirectory(exportDirectory);
        File.WriteAllBytes(Path.Combine(exportDirectory, "report.txt"), [9, 9, 9]);

        ExportResult renamed = await session.ExportAsync([id], exportDirectory, new ExportOptions(), null, CancellationToken.None);
        Assert.Contains(renamed.Issues, issue => issue.Kind == ExportIssueKind.Renamed);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(exportDirectory, "report (2).txt")));

        ExportResult skipped = await session.ExportAsync(
            [id], exportDirectory, new ExportOptions(Conflict: ConflictPolicy.Skip), null, CancellationToken.None);
        Assert.Equal(0, skipped.FilesWritten);
        Assert.Contains(skipped.Issues, issue => issue.Kind == ExportIssueKind.Skipped);
    }

    [Fact]
    public async Task Export_restores_timestamps_and_marks_the_file_as_downloaded()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("stamped.bin", VaultTestContext.Bytes(32, 23));
        var written = new DateTime(2024, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);

        await using IVaultSession session = await context.CreateAsync();
        ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

        string exportDirectory = Path.Combine(context.Root, "export");
        await session.ExportAsync(
            [imported.Imported.Single()], exportDirectory, new ExportOptions(), null, CancellationToken.None);

        string exported = Path.Combine(exportDirectory, "stamped.bin");
        Assert.Equal(written, File.GetLastWriteTimeUtc(exported));

        string zone = File.ReadAllText(exported + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_refuses_to_leave_the_export_root()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        string exportDirectory = Path.Combine(context.Root, "export");

        ExportResult result = await session.ExportAsync([folder], exportDirectory, new ExportOptions(), null, CancellationToken.None);

        Assert.Equal(1, result.FoldersCreated);
        Assert.True(Directory.Exists(Path.Combine(exportDirectory, "Documents")));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Progress_reports_start_and_completion_at_most_once_per_step()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("progress.bin", VaultTestContext.Bytes(MultiChunkLength, 27));

        var reports = new List<VaultProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), progress, CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.All(reports, report => Assert.Equal(VaultOperation.Import, report.Operation));
        Assert.Equal(0L, reports[0].BytesDone);
        Assert.False(reports[^1].IsCancellable);
        Assert.True(reports.Count <= 4, $"expected at most four reports for {MultiChunkLength} bytes, got {reports.Count}");
    }

    /// <summary>Creates a junction; returns false when the platform or the policy refuses.</summary>
    /// <param name="link">Path of the junction.</param>
    /// <param name="target">Directory it should point at.</param>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit(10_000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>A progress sink that records on the reporting thread, so the test can count reports.</summary>
    private sealed class SynchronousProgress : IProgress<VaultProgress>
    {
        private readonly Action<VaultProgress> _sink;
        private readonly object _gate = new();

        /// <summary>Creates the sink.</summary>
        /// <param name="sink">Callback for every report.</param>
        public SynchronousProgress(Action<VaultProgress> sink) => _sink = sink;

        /// <inheritdoc />
        public void Report(VaultProgress value)
        {
            lock (_gate)
            {
                _sink(value);
            }
        }
    }
}
