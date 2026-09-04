using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Session;

/// <summary>Creating, reopening and the state a session reports about itself.</summary>
public sealed class VaultLifecycleTests
{
    [Fact]
    public async Task Create_writes_a_readable_vault_with_save_counter_one()
    {
        using var context = new VaultTestContext();

        await using (IVaultSession session = await context.CreateAsync())
        {
            Assert.Equal(context.VaultPath, session.Path);
            Assert.False(session.IsDirty);
            Assert.False(session.IsLocked);
            Assert.False(session.IsReadOnly);
            Assert.Equal(1ul, session.Statistics.SaveCounter);
            Assert.Empty(session.GetChildren(EntryId.Root));
        }

        Assert.True(File.Exists(context.VaultPath));

        VaultHeaderInfo info = await context.NewFactory().ReadHeaderAsync(context.VaultPath, CancellationToken.None);
        Assert.Equal((ushort)1, info.FormatVersion);
        Assert.Equal(VaultTestContext.FastKdf, info.Kdf);
        Assert.Equal(new FileInfo(context.VaultPath).Length, info.FileLength);
        Assert.Equal(VaultTestContext.FastKdf.MemoryBytes, info.RequiredMemoryBytes);
    }

    [Fact]
    public async Task Create_refuses_an_existing_path()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(async () =>
        {
            await using IVaultSession _ = await context.CreateAsync();
        });

        Assert.Equal(VaultErrorCode.IoError, error.Code);
    }

    [Fact]
    public async Task Reopen_with_the_wrong_password_fails_authentication()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        VaultAuthenticationException error = await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync(VaultTestContext.OtherPassword);
        });

        Assert.Equal(VaultErrorCode.AuthenticationFailed, error.Code);
    }

    [Fact]
    public async Task Reopen_with_the_wrong_keyfile_fails_authentication()
    {
        using var context = new VaultTestContext();
        using KeyFile good = KeyFile.FromBytes(VaultTestContext.Bytes(64, 1));
        using KeyFile bad = KeyFile.FromBytes(VaultTestContext.Bytes(64, 2));

        await using (IVaultSession session = await context.CreateAsync(keyFile: good))
        {
        }

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync(keyFile: bad);
        });

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        await using IVaultSession reopened = await context.OpenAsync(keyFile: good);
        Assert.Equal(1ul, reopened.Statistics.SaveCounter);
    }

    [Fact]
    public async Task Reading_the_header_of_a_foreign_file_reports_that_it_is_not_a_vault()
    {
        using var context = new VaultTestContext();
        string path = context.WriteSourceFile("not-a-vault.bastion", VaultTestContext.Bytes(4096, 7));

        VaultFormatException error = await Assert.ThrowsAsync<VaultFormatException>(
            () => context.NewFactory().ReadHeaderAsync(path, CancellationToken.None));

        Assert.Equal(VaultErrorCode.NotAVault, error.Code);
    }

    [Fact]
    public async Task Save_increments_the_counter_and_clears_the_dirty_flag()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        Assert.True(session.IsDirty);
        Assert.Equal(1, session.Pending.Added);

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.False(session.IsDirty);
        Assert.Equal(2ul, session.Statistics.SaveCounter);
        Assert.False(session.Pending.Any);
        Assert.Equal(context.Clock.UtcNow, session.Statistics.LastSavedUtc);
    }

    [Fact]
    public async Task Statistics_count_folders_files_and_bytes()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        string small = context.WriteSourceFile("small.bin", VaultTestContext.Bytes(3, 11));
        string bigger = context.WriteSourceFile("bigger.bin", VaultTestContext.Bytes(5000, 12));

        await session.ImportAsync(folder, [small, bigger], new ImportOptions(), null, CancellationToken.None);

        VaultStatistics statistics = session.Statistics;
        Assert.Equal(1, statistics.FolderCount);
        Assert.Equal(2, statistics.FileCount);
        Assert.Equal(5003L, statistics.TotalPlaintextBytes);
        Assert.False(statistics.OpenedFromIndexCopy);

        EntryInfo? info = session.Find(folder);
        Assert.NotNull(info);
        Assert.Equal(5003L, info.Length);
        Assert.Equal(2, info.ChildCount);
    }

    [Fact]
    public async Task A_read_only_file_opens_read_only_and_refuses_mutations()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        File.SetAttributes(context.VaultPath, File.GetAttributes(context.VaultPath) | FileAttributes.ReadOnly);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.True(reopened.IsReadOnly);

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => reopened.CreateFolderAsync(EntryId.Root, "Nope", CancellationToken.None));
        Assert.Equal(VaultErrorCode.ReadOnlySession, error.Code);

        VaultOperationException saveError = await Assert.ThrowsAsync<VaultOperationException>(
            () => reopened.SaveAsync(SaveOptions.Default, null, CancellationToken.None));
        Assert.Equal(VaultErrorCode.ReadOnlySession, saveError.Code);
    }

    [Fact]
    public async Task Opening_read_only_by_option_refuses_mutations()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        await using IVaultSession reopened = await context.OpenAsync(options: new OpenOptions(ReadOnly: true));
        Assert.True(reopened.IsReadOnly);
        await Assert.ThrowsAsync<VaultOperationException>(
            () => reopened.CreateFolderAsync(EntryId.Root, "Nope", CancellationToken.None));
    }

    [Fact]
    public async Task A_vault_that_changed_on_disk_is_not_overwritten()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();
        await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);

        // Replace the file underneath the session; the session shares delete access, so this is possible.
        byte[] bytes = File.ReadAllBytes(context.VaultPath);
        File.Delete(context.VaultPath);
        File.WriteAllBytes(context.VaultPath, [.. bytes, 0x00]);

        VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(
            () => session.SaveAsync(SaveOptions.Default, null, CancellationToken.None));

        Assert.Equal(VaultErrorCode.ChangedOnDisk, error.Code);
        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));
    }

    [Fact]
    public async Task A_second_operation_while_one_runs_reports_busy()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        var resolverGate = new TaskCompletionSource();
        var conflictReached = new TaskCompletionSource();

        string first = context.WriteSourceFile("clash.bin", VaultTestContext.Bytes(64, 21));
        await session.ImportAsync(EntryId.Root, [first], new ImportOptions(), null, CancellationToken.None);

        var options = new ImportOptions(ConflictResolver: async (_, _) =>
        {
            conflictReached.TrySetResult();
            await resolverGate.Task.ConfigureAwait(false);
            return ConflictDecision.Skip;
        });

        Task<ImportResult> running = session.ImportAsync(EntryId.Root, [first], options, null, CancellationToken.None);
        await conflictReached.Task;

        Assert.True(session.IsBusy);
        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None));
        Assert.Equal(VaultErrorCode.Busy, error.Code);

        resolverGate.SetResult();
        await running;
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task Snapshot_reads_stay_available_while_an_operation_runs()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        var resolverGate = new TaskCompletionSource();
        var conflictReached = new TaskCompletionSource();
        string first = context.WriteSourceFile("clash.bin", VaultTestContext.Bytes(64, 22));
        await session.ImportAsync(EntryId.Root, [first], new ImportOptions(), null, CancellationToken.None);

        var options = new ImportOptions(ConflictResolver: async (_, _) =>
        {
            conflictReached.TrySetResult();
            await resolverGate.Task.ConfigureAwait(false);
            return ConflictDecision.Skip;
        });

        Task<ImportResult> running = session.ImportAsync(EntryId.Root, [first], options, null, CancellationToken.None);
        await conflictReached.Task;

        Assert.Single(session.GetChildren(EntryId.Root));
        Assert.Equal("\\clash.bin", session.FormatPath(VaultTestContext.Entry(session, "clash.bin").Id));

        resolverGate.SetResult();
        await running;
    }

    [Fact]
    public async Task Paths_resolve_case_insensitively_and_format_back()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId documents = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        EntryId year = await session.CreateFolderAsync(documents, "2026", CancellationToken.None);

        Assert.Equal("\\Documents\\2026", session.FormatPath(year));
        Assert.Equal("\\", session.FormatPath(EntryId.Root));

        Assert.True(session.TryResolvePath("\\documents\\2026", out EntryId resolved));
        Assert.Equal(year, resolved);
        Assert.False(session.TryResolvePath("\\Documents\\2027", out EntryId missing));
        Assert.Equal(EntryId.Root, missing);

        IReadOnlyList<EntryInfo> ancestors = session.GetAncestors(year);
        Assert.Equal(new[] { "Documents", "2026" }, ancestors.Select(entry => entry.Name));
    }

    [Fact]
    public async Task Children_are_ordered_folders_first_then_naturally_by_name()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.ImportAsync(
            EntryId.Root,
            [
                context.WriteSourceFile("file10.bin", VaultTestContext.Bytes(1, 1)),
                context.WriteSourceFile("file2.bin", VaultTestContext.Bytes(1, 2)),
            ],
            new ImportOptions(),
            null,
            CancellationToken.None);

        await session.CreateFolderAsync(EntryId.Root, "Zulu", CancellationToken.None);
        await session.CreateFolderAsync(EntryId.Root, "alpha", CancellationToken.None);

        Assert.Equal(
            new[] { "alpha", "Zulu", "file2.bin", "file10.bin" },
            session.GetChildren(EntryId.Root).Select(entry => entry.Name));
    }

    [Fact]
    public async Task Name_validation_reports_conflicts_and_suggests_a_free_name()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);

        Assert.True(session.ValidateName(EntryId.Root, "Other").IsValid);

        NameCheck conflict = session.ValidateName(EntryId.Root, "documents");
        Assert.False(conflict.IsValid);
        Assert.Equal("documents (2)", conflict.Suggestion);

        Assert.True(session.ValidateName(EntryId.Root, "Documents", folder).IsValid);
        Assert.False(session.ValidateName(EntryId.Root, "bad:name").IsValid);
    }

    [Fact]
    public async Task Sweeping_removes_a_stale_staging_container()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        string orphan = context.VaultPath + "~stage-" + Guid.NewGuid().ToString("D");
        File.WriteAllBytes(orphan, VaultTestContext.Bytes(4096, 31));
        string temp = context.VaultPath + ".tmp-0a1b2c3d";
        File.WriteAllBytes(temp, VaultTestContext.Bytes(1024, 32));

        long reclaimed = await context.NewFactory().SweepOrphansAsync([context.Root], CancellationToken.None);

        Assert.Equal(5120L, reclaimed);
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(context.VaultPath));
    }

    [Fact]
    public async Task The_index_length_stays_at_the_documented_minimum_for_a_small_vault()
    {
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
        }

        VaultHeaderInfo info = await context.NewFactory().ReadHeaderAsync(context.VaultPath, CancellationToken.None);
        Assert.Equal(VaultLimits.MinIndexLength, info.IndexLength);
        Assert.Equal(VaultHeader.Size + (2 * info.IndexLength), info.FileLength);
    }
}
