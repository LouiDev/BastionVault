using System.Diagnostics;
using BastionVault.Core.Crypto;
using BastionVault.Core.Format;
using BastionVault.Core.Session;

namespace BastionVault.Core.Tests.Session;

/// <summary>
/// Regressions for defects found in review: export through a junction, a concurrent lock during a save,
/// the orphan sweep pattern, the staging pre-flight after a spill, buffers sized from an attacker-chosen
/// chunk size, the non-cancellable progress signal, the comment character filter, and the uniquifier
/// that could put an unsavable name into the tree.
/// </summary>
public sealed class ReviewRegressionTests
{
    /// <summary>Big enough that the save reports progress at least once while it is still writing.</summary>
    private const int LargeEnoughForAProgressReport = 9 * 1024 * 1024;

    /// <summary>
    /// A folder destination that is a reparse point is refused, and so is everything under it. Refusing
    /// only the leaf let the descendants be written straight through the junction, outside the export
    /// root (FORMAT.md section 6.4).
    /// </summary>
    [Fact]
    public async Task Export_refuses_the_whole_subtree_under_a_junction_it_will_not_follow()
    {
        using var context = new VaultTestContext();

        string outside = Path.Combine(context.Root, "outside");
        Directory.CreateDirectory(outside);

        string exportRoot = Path.Combine(context.Root, "export");
        Directory.CreateDirectory(exportRoot);

        string junction = Path.Combine(exportRoot, "Startup");
        if (!TryCreateJunction(junction, outside))
        {
            return;
        }

        await using IVaultSession session = await context.CreateAsync();
        EntryId startup = await session.CreateFolderAsync(EntryId.Root, "Startup", CancellationToken.None);
        EntryId programs = await session.CreateFolderAsync(startup, "Programs", CancellationToken.None);

        string payload = context.WriteSourceFile("evil.lnk", "PWNED"u8.ToArray());
        await session.ImportAsync(programs, [payload], new ImportOptions(), null, CancellationToken.None);

        ExportResult result = await session.ExportAsync(
            [startup], exportRoot, new ExportOptions(), null, CancellationToken.None);

        Assert.Contains(result.Issues, issue => issue.Kind == ExportIssueKind.ReparsePointRefused);
        Assert.Equal(0, result.FilesWritten);
        Assert.Empty(Directory.GetFileSystemEntries(outside, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Even when the junction is not the refused entry itself but a component on the way, no byte may
    /// land outside the export root.
    /// </summary>
    [Fact]
    public async Task Export_refuses_a_destination_whose_parent_component_is_a_junction()
    {
        using var context = new VaultTestContext();

        string outside = Path.Combine(context.Root, "outside");
        Directory.CreateDirectory(outside);

        string exportRoot = Path.Combine(context.Root, "export");
        Directory.CreateDirectory(exportRoot);

        string junction = Path.Combine(exportRoot, "Docs");
        if (!TryCreateJunction(junction, outside))
        {
            return;
        }

        await using IVaultSession session = await context.CreateAsync();
        EntryId docs = await session.CreateFolderAsync(EntryId.Root, "Docs", CancellationToken.None);
        string payload = context.WriteSourceFile("note.txt", "secret"u8.ToArray());
        EntryId file = (await session.ImportAsync(docs, [payload], new ImportOptions(), null, CancellationToken.None)).Imported[0];

        // Exporting the file alone gives the plan a relative path whose first component is the junction.
        ExportResult result = await session.ExportAsync(
            [file], junction, new ExportOptions(), null, CancellationToken.None);

        // The junction is the export root here, which the user chose, so this one is allowed to write.
        Assert.Equal(1, result.FilesWritten);

        ExportResult viaParent = await session.ExportAsync(
            [docs], exportRoot, new ExportOptions(), null, CancellationToken.None);

        Assert.Contains(viaParent.Issues, issue => issue.Kind == ExportIssueKind.ReparsePointRefused);
        Assert.Equal(0, viaParent.FilesWritten);
    }

    /// <summary>
    /// <see cref="IVaultSession.Lock"/> runs on any thread without the operation gate. A save must not
    /// report its own success as a data-integrity failure just because the session key set went away
    /// underneath it, and it must still adopt what it wrote (FORMAT.md sections 8.3 step 9 and 8.8).
    /// </summary>
    [Fact]
    public async Task A_lock_during_a_save_neither_fails_the_save_nor_leaves_the_session_stale()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("big.bin", VaultTestContext.Bytes(LargeEnoughForAProgressReport, 11));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

        var locker = new LockOnFirstByteReport(session);
        await session.SaveAsync(SaveOptions.Default, locker, CancellationToken.None);

        Assert.True(locker.Fired, "the save never reported progress while it was still writing");
        Assert.True(session.IsLocked);
        Assert.False(session.IsDirty);
        Assert.Empty(Directory.GetFiles(context.Root, "*.bak-*"));
        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));

        // The session adopted the file it wrote, so the next save must not see it as changed on disk.
        using (Passphrase passphrase = Passphrase.FromString(VaultTestContext.Password))
        {
            await session.UnlockAsync(passphrase, null, null, CancellationToken.None);
        }

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal(1, reopened.Statistics.FileCount);
        Assert.Equal(
            VaultTestContext.Digest(VaultTestContext.Bytes(LargeEnoughForAProgressReport, 11)),
            VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(reopened, VaultTestContext.Entry(reopened, "big.bin").Id)));
    }

    /// <summary>
    /// The save temporary is named after the vault file, whatever its extension, so the sweep pattern
    /// must not assume <c>.bastion</c> (FORMAT.md section 8.5).
    /// </summary>
    [Fact]
    public async Task Orphan_sweep_reclaims_the_temporary_of_a_vault_that_is_not_named_bastion()
    {
        using var context = new VaultTestContext();
        string orphan = Path.Combine(context.Root, "archive.vault.tmp-1a2b3c4d");
        await File.WriteAllBytesAsync(orphan, new byte[64], CancellationToken.None);

        long reclaimed = await new VaultFactory().SweepOrphansAsync([context.Root], CancellationToken.None);

        Assert.Equal(64, reclaimed);
        Assert.False(File.Exists(orphan));
    }

    /// <summary>
    /// Once staging has spilled, every further byte goes to the container even though the in-memory
    /// counter is back to zero, so the pre-flight must keep checking the staging volume.
    /// </summary>
    [Fact]
    public void Staging_preflight_still_targets_the_disk_after_the_store_has_spilled()
    {
        using var context = new VaultTestContext();
        string vaultPath = Path.Combine(context.Root, "staging.bastion");
        File.WriteAllBytes(vaultPath, new byte[16]);

        var options = new OpenOptions(InMemoryStagingLimit: 1024);
        using var store = new StagingStore(vaultPath, new DefaultVaultPaths(new DeterministicRandomSource(3)), options, Guid.NewGuid());

        Assert.False(store.WouldStageToDisk(100));

        StagedBlobSource slot = store.BeginBlob();
        store.Append(slot, new byte[2048]);
        store.EndBlob(slot);

        Assert.True(store.WouldStageToDisk(100), "a small import after a spill is still written to the container");
    }

    /// <summary>
    /// The chunk size is an index field an attacker picks freely; buffers must follow the blob's real
    /// per-chunk length instead, or a vault of one-byte files costs 128 MiB per entry to read.
    /// </summary>
    [Fact]
    public void Blob_buffers_are_sized_from_the_blob_not_from_the_declared_chunk_size()
    {
        using VaultCrypto crypto = VaultCrypto.Create(new DeterministicRandomSource(7));
        byte[] blobId = new byte[16];

        using var reader = new BlobReader(
            new FakeBlobSource(1 + ChunkCipher.TagSize), crypto, blobId, 1, VaultLimits.MaxChunkSize, "\\tiny.bin");

        Assert.Equal(1, reader.MaxChunkPlaintextLength);
        Assert.Equal(1 + ChunkCipher.TagSize, reader.MaxChunkCiphertextLength);

        // Per-thread accounting on purpose: the process-wide counter picks up whatever the other
        // test threads allocate meanwhile (the Argon2 tests alone move megabytes), which made
        // this assertion flaky in CI. Everything measured here runs synchronously on this thread.
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (var stream = new DecryptingBlobStream(new BlobReader(
            new FakeBlobSource(1 + ChunkCipher.TagSize), crypto, blobId, 1, VaultLimits.MaxChunkSize, "\\tiny.bin")))
        {
            Assert.Equal(1, stream.Length);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"opening a one-byte blob allocated {allocated} bytes");
    }

    /// <summary>
    /// API.md's cancellation table promises a report with <c>IsCancellable = false</c> once the save
    /// passes the point of no return. The byte throttle must not swallow it on a small vault.
    /// </summary>
    [Fact]
    public async Task A_save_announces_the_phase_that_can_no_longer_be_cancelled()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("small.bin", VaultTestContext.Bytes(200_000, 12));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

        List<VaultProgress> reports = [];
        var sink = new CollectingProgress(reports);
        await session.SaveAsync(SaveOptions.Default, sink, CancellationToken.None);

        int lastCancellable = reports.FindLastIndex(report => report.IsCancellable);
        int firstNonCancellable = reports.FindIndex(report => !report.IsCancellable);

        Assert.True(firstNonCancellable >= 0, "the save never announced its non-cancellable phase");
        Assert.True(
            firstNonCancellable < reports.Count - 1 || lastCancellable < 0,
            "the only non-cancellable report was the completion report, which is too late to hide Cancel");
    }

    /// <summary>
    /// Comments are rendered verbatim in the Properties dialog, so they get the same invisible-character
    /// filter as names; otherwise a crafted comment can reverse or split the text a user reads.
    /// </summary>
    [Theory]
    [InlineData("\u202Eexe.txt")]
    [InlineData("first\u2028second")]
    [InlineData("zero\u200Bwidth")]
    public async Task A_comment_may_not_contain_invisible_formatting_characters(string comment)
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();
        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Docs", CancellationToken.None);

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.SetCommentAsync(folder, comment, CancellationToken.None));

        Assert.Equal(VaultErrorCode.NameInvalid, error.Code);
    }

    [Fact]
    public async Task A_comment_may_still_span_several_lines()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();
        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Docs", CancellationToken.None);

        await session.SetCommentAsync(folder, "first line\r\nsecond line\tindented", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal("first line\r\nsecond line\tindented", VaultTestContext.Entry(reopened, "Docs").Comment);
    }

    /// <summary>
    /// Every classification in <see cref="IoGuard"/> hangs off the Win32 status inside the HRESULT. The
    /// mask has to be an <see cref="int"/>: with an unsigned one the comparison is promoted to
    /// <see cref="long"/>, the sign-extended HRESULT never matches, and DiskFull, Locked and
    /// ReadOnlyTarget silently become IoError while the replace retry loop never retries.
    /// </summary>
    [Theory]
    [InlineData(0x20, VaultErrorCode.Locked)]
    [InlineData(0x21, VaultErrorCode.Locked)]
    [InlineData(0x27, VaultErrorCode.DiskFull)]
    [InlineData(0x70, VaultErrorCode.DiskFull)]
    [InlineData(0x13, VaultErrorCode.ReadOnlyTarget)]
    [InlineData(0x15, VaultErrorCode.ReadOnlyTarget)]
    [InlineData(0x02, VaultErrorCode.IoError)]
    public void An_io_failure_is_classified_by_its_win32_code(int win32, VaultErrorCode expected)
    {
        var failure = new IOException("probe", unchecked((int)0x80070000) | win32);

        Assert.Equal(expected, IoGuard.CodeFor(failure));
    }

    [Theory]
    [InlineData(0x20, true)]
    [InlineData(0x21, true)]
    [InlineData(0x497, true)]
    [InlineData(0x498, true)]
    [InlineData(0x499, true)]
    [InlineData(0x05, false)]
    public void A_replace_failure_is_retried_only_for_the_documented_codes(int win32, bool transient)
    {
        var failure = new IOException("probe", unchecked((int)0x80070000) | win32);

        Assert.Equal(transient, IoGuard.IsTransientReplaceFailure(failure));
    }

    [Theory]
    [InlineData(0x11, true)]
    [InlineData(0x57, true)]
    [InlineData(0x20, false)]
    public void A_replace_that_the_file_system_cannot_do_falls_back_to_two_moves(int win32, bool unsupported)
    {
        var failure = new IOException("probe", unchecked((int)0x80070000) | win32);

        Assert.Equal(unsupported, IoGuard.IsReplaceUnsupported(failure));
    }

    /// <summary>
    /// A sharing violation that survives every retry must be reported as Locked by the explicit report of
    /// FORMAT.md section 8.3 step 6, which the old attempt guard made unreachable.
    /// </summary>
    [Fact]
    public async Task A_replace_that_never_succeeds_ends_on_the_documented_Locked_report()
    {
        using var context = new VaultTestContext();
        string source = context.WriteSourceFile("held.bin", VaultTestContext.Bytes(64, 13));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

        // A reader that refuses to share deletion keeps File.Replace failing for the whole retry budget.
        using (new FileStream(context.VaultPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(
                () => session.SaveAsync(SaveOptions.Default, null, CancellationToken.None));

            Assert.Equal(VaultErrorCode.Locked, error.Code);
            Assert.Contains("after 6 attempts", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));
        Assert.True(session.IsDirty);
    }

    /// <summary>
    /// In the two-move fallback the temporary file becomes the only copy of the new vault after the first
    /// move, so it must be taken out of the cleanup path and a failure must name every path that holds
    /// the user's data (FORMAT.md section 8.3).
    /// </summary>
    [Fact]
    public void The_two_move_fallback_keeps_the_new_vault_and_names_where_the_data_is()
    {
        using var context = new VaultTestContext();
        string destination = Path.Combine(context.Root, "move.bastion");
        string temp = destination + ".tmp-deadbeef";
        string backup = destination + ".bak-deadbeef";

        File.WriteAllBytes(destination, [1]);
        File.WriteAllBytes(temp, [2]);

        bool consumed = false;
        Assert.Equal(backup, SaveWriter.MoveIntoPlace(destination, temp, backup, () => consumed = true));
        Assert.True(consumed);
        Assert.Equal<byte[]>([2], File.ReadAllBytes(destination));
        Assert.Equal<byte[]>([1], File.ReadAllBytes(backup));

        // Now the second move fails: the temporary file is gone, and the destination no longer exists.
        File.Delete(temp);
        File.Move(destination, temp);
        File.Delete(backup);
        File.WriteAllBytes(destination, [3]);
        File.Delete(temp);

        consumed = false;
        VaultIoException error = Assert.Throws<VaultIoException>(
            () => SaveWriter.MoveIntoPlace(destination, temp, backup, () => consumed = true));

        Assert.True(consumed, "the temporary file must be taken out of the cleanup path before the second move");
        Assert.Contains(temp, error.Message, StringComparison.Ordinal);
        Assert.Contains(backup, error.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(backup), "the previous version must still be reachable at the path the message names");
    }

    /// <summary>
    /// FORMAT.md section 3.1 step 9 measures INSTALLED physical memory, and nothing else. Measuring what
    /// is free made the verdict depend on the machine's load at that instant: the default 512 MiB preset
    /// was refused with ResourceLimit on a 32 GiB machine that happened to be busy. The installed total
    /// does not move, so the same header gets the same answer twice in a row.
    /// </summary>
    [Fact]
    public void The_kdf_preflight_measures_installed_memory_and_does_not_move_with_load()
    {
        long installed = Credentials.InstalledPhysicalMemoryBytes();

        Assert.True(installed > 0, "the pre-flight found no memory at all");
        Assert.Equal(installed, Credentials.InstalledPhysicalMemoryBytes());

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        if (info.MemoryLoadBytes > 0)
        {
            Assert.True(
                installed >= info.MemoryLoadBytes,
                "installed memory cannot be less than what the machine currently holds");
        }

        // The default preset is what the UI offers, and it must not be refusable on a machine of any
        // ordinary size: 512 MiB against 75 % of everything installed.
        long budget = (long)(installed * VaultLimits.KdfMemoryFractionOfInstalled);
        if (installed >= 2L * 1024 * 1024 * 1024)
        {
            Assert.True(
                KdfParameters.Default.MemoryBytes <= budget,
                "the default preset must pass the pre-flight on any machine with 2 GiB or more");
            Credentials.PreflightMemory(KdfParameters.Default);
        }
    }

    /// <summary>
    /// FORMAT.md section 6.4 requires the extended-length prefix for long export paths, because Windows
    /// only honours the manifest flag when the registry opt-in is set as well.
    /// </summary>
    [Fact]
    public void Long_paths_get_the_extended_length_prefix_and_short_ones_do_not()
    {
        string shortPath = @"C:\vault\export\file.bin";
        Assert.Equal(shortPath, LongPath.ForIo(shortPath));

        string longPath = @"C:\vault\" + new string('a', 300) + @"\file.bin";
        Assert.Equal(@"\\?\" + longPath, LongPath.ForIo(longPath));

        string unc = @"\\server\share\" + new string('b', 300) + @"\file.bin";
        Assert.Equal(@"\\?\UNC\server\share\" + new string('b', 300) + @"\file.bin", LongPath.ForIo(unc));

        Assert.Equal(LongPath.ForIo(longPath), LongPath.ForIo(LongPath.ForIo(longPath)));
        Assert.Equal(string.Empty, LongPath.ForIo(string.Empty));
    }

    /// <summary>An export whose destination passes MAX_PATH still writes the file.</summary>
    [Fact]
    public async Task Export_writes_a_destination_that_is_longer_than_max_path()
    {
        using var context = new VaultTestContext();
        string deep = new('d', 120);

        await using IVaultSession session = await context.CreateAsync();
        EntryId first = await session.CreateFolderAsync(EntryId.Root, deep, CancellationToken.None);
        EntryId second = await session.CreateFolderAsync(first, deep, CancellationToken.None);

        string payload = context.WriteSourceFile("deep.bin", VaultTestContext.Bytes(64, 14));
        await session.ImportAsync(second, [payload], new ImportOptions(), null, CancellationToken.None);

        string exportRoot = Path.Combine(context.Root, "export");
        ExportResult result = await session.ExportAsync(
            [first], exportRoot, new ExportOptions(), null, CancellationToken.None);

        string expected = Path.Combine(exportRoot, deep, deep, "deep.bin");
        Assert.True(expected.Length > 259, $"the destination is only {expected.Length} characters long");
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(LongPath.ForIo(expected)), "the exported file is missing");
    }

    /// <summary>
    /// A mutation may never put a name into the tree that the index serializer will refuse: the entry is
    /// accepted here but every later save fails with <c>IndexInvalid</c>, and the message names it only
    /// by ordinal (FORMAT.md section 6.1).
    /// </summary>
    [Fact]
    public async Task A_copy_that_collides_never_produces_a_name_the_index_refuses()
    {
        using var context = new VaultTestContext();
        string name = "a." + new string('b', 253);
        Assert.Equal(255, name.Length);

        await using IVaultSession session = await context.CreateAsync();
        EntryId folder = await session.CreateFolderAsync(EntryId.Root, name, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        IReadOnlyList<EntryId> copies = await session.CopyAsync([folder], EntryId.Root, CancellationToken.None);

        EntryInfo copy = session.GetChildren(EntryId.Root).Single(entry => entry.Id == copies[0]);
        Assert.True(EntryNames.Validate(copy.Name).IsValid, $"the copy is named \"{copy.Name}\" ({copy.Name.Length} code units)");

        // The point of the check: the vault must still be savable and reopenable afterwards.
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal(2, reopened.GetChildren(EntryId.Root).Count);
    }

    /// <summary>
    /// An import that collides goes through the same uniquifier, so it gets the same guarantee.
    /// </summary>
    [Fact]
    public async Task An_import_that_collides_never_produces_a_name_the_index_refuses()
    {
        using var context = new VaultTestContext();
        string name = "a." + new string('b', 253);
        string source = context.WriteSourceFile(name, VaultTestContext.Bytes(32, 21));

        await using IVaultSession session = await context.CreateAsync();
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
        await session.ImportAsync(
            EntryId.Root, [source], new ImportOptions(Conflict: ConflictPolicy.Rename), null, CancellationToken.None);

        foreach (EntryInfo entry in session.GetChildren(EntryId.Root))
        {
            Assert.True(EntryNames.Validate(entry.Name).IsValid, $"\"{entry.Name}\" is {entry.Name.Length} code units long");
        }

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
    }

    /// <summary>
    /// U+2028 and U+2029 split a fixed-height list row in two, hiding the real extension, so they are
    /// refused on every mutation just like the bidi overrides are.
    /// </summary>
    [Theory]
    [InlineData("invoice.pdf\u2028payload.exe")]
    [InlineData("invoice.pdf\u2029payload.exe")]
    [InlineData("zero\u200Bwidth.txt")]
    public async Task A_name_may_not_contain_invisible_formatting_characters(string name)
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.CreateFolderAsync(EntryId.Root, name, CancellationToken.None));

        Assert.Equal(VaultErrorCode.NameInvalid, error.Code);
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

    /// <summary>A blob source that hands out zeroed ciphertext; only the buffer sizes matter here.</summary>
    private sealed class FakeBlobSource : IBlobSource
    {
        /// <summary>Creates a source of a fixed ciphertext length.</summary>
        /// <param name="length">Ciphertext length of the blob.</param>
        public FakeBlobSource(long length) => Length = length;

        /// <inheritdoc />
        public long Length { get; }

        /// <inheritdoc />
        public void Read(long offset, Span<byte> destination) => destination.Clear();
    }

    /// <summary>Locks the session from the save's own progress callback, while the temp is still open.</summary>
    private sealed class LockOnFirstByteReport : IProgress<VaultProgress>
    {
        private readonly IVaultSession _session;

        /// <summary>Creates the sink.</summary>
        /// <param name="session">Session to lock.</param>
        public LockOnFirstByteReport(IVaultSession session) => _session = session;

        /// <summary>True once the sink saw a mid-write report and locked the session.</summary>
        public bool Fired { get; private set; }

        /// <inheritdoc />
        public void Report(VaultProgress value)
        {
            if (Fired || value.BytesDone <= 0 || !value.IsCancellable)
            {
                return;
            }

            Fired = true;
            _session.Lock();
        }
    }

    /// <summary>Collects every report on the reporting thread.</summary>
    private sealed class CollectingProgress : IProgress<VaultProgress>
    {
        private readonly List<VaultProgress> _reports;
        private readonly Lock _gate = new();

        /// <summary>Creates the sink.</summary>
        /// <param name="reports">List the reports are appended to.</param>
        public CollectingProgress(List<VaultProgress> reports) => _reports = reports;

        /// <inheritdoc />
        public void Report(VaultProgress value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }
    }
}
