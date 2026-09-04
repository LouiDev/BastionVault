using System.Security.Cryptography;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// Two sessions on one file, one operation at a time inside a session, and what a cancelled long
/// operation is allowed to leave behind. This is the part of API.md rule 3 and of the save state machine
/// of FORMAT.md section 8.3 that only shows up when something else is happening at the same time.
/// </summary>
public sealed class ConcurrencyTests
{
    /// <summary>Password of every vault in this class.</summary>
    private const string Password = TamperVault.Password;

    /// <summary>Size of the file the cancellation tests work on: several progress steps long.</summary>
    private const int LargeFileLength = 12 * 1024 * 1024;

    [Fact]
    public async Task A_second_session_refuses_to_save_over_the_changes_of_the_first()
    {
        using var work = new TempDirectory("two-sessions");
        string path = Path.Combine(work.Path, "shared.bastion");
        await using (IVaultSession creator = await CreateAsync(path, work))
        {
        }

        await using IVaultSession first = await OpenAsync(path, 31);
        await using IVaultSession second = await OpenAsync(path, 32);

        Assert.Equal(first.Statistics.SaveCounter, second.Statistics.SaveCounter);

        await first.CreateFolderAsync(EntryId.Root, "written by the first session", CancellationToken.None);
        await first.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await second.CreateFolderAsync(EntryId.Root, "written by the second session", CancellationToken.None);
        VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(
            () => second.SaveAsync(SaveOptions.Default, null, CancellationToken.None));

        VaultAssert.Failure(error, VaultErrorCode.ChangedOnDisk, "the file changed under the second session");
        Assert.Equal(path, error.OffendingPath);
        Assert.True(second.IsDirty, "a refused save keeps the unsaved work");
        Assert.Empty(Directory.GetFiles(work.Path, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.bak-*"));

        // The file on disk is exactly what the first session wrote.
        await using IVaultSession third = await OpenAsync(path, 33);
        Assert.Equal(
            ["written by the first session"],
            third.GetChildren(EntryId.Root).Where(entry => entry.Kind == EntryKind.Folder).Select(entry => entry.Name).ToArray());
        Assert.True((await third.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    [Fact]
    public async Task A_deleted_vault_file_is_reported_instead_of_being_recreated_by_a_save()
    {
        using var work = new TempDirectory("deleted-under-session");
        string path = Path.Combine(work.Path, "gone.bastion");
        await using (IVaultSession creator = await CreateAsync(path, work))
        {
        }

        await using IVaultSession session = await OpenAsync(path, 34);
        await session.CreateFolderAsync(EntryId.Root, "pending", CancellationToken.None);

        // The session shares delete access, so the file really can disappear underneath it.
        File.Delete(path);

        VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(
            () => session.SaveAsync(SaveOptions.Default, null, CancellationToken.None));

        VaultAssert.Failure(error, VaultErrorCode.ChangedOnDisk, "the vault file was deleted");
        Assert.False(File.Exists(path), "a refused save must not resurrect the file");
        Assert.Empty(Directory.GetFiles(work.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task Every_operation_is_refused_as_busy_while_an_import_is_running()
    {
        using var work = new TempDirectory("busy");
        string path = Path.Combine(work.Path, "busy.bastion");
        string sources = work.SubDirectory("source");
        string file = Path.Combine(sources, "clash.bin");
        await File.WriteAllBytesAsync(file, "content"u8.ToArray());

        await using IVaultSession session = await CreateAsync(path, work);
        await session.ImportAsync(
            EntryId.Root, [file], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);
        Assert.True(session.TryResolvePath("\\clash.bin", out EntryId existing));

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var options = new ImportOptions(
            PreserveTimestamps: false,
            ConflictResolver: async (_, _) =>
            {
                reached.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return ConflictDecision.Skip;
            });

        Task<ImportResult> running = session.ImportAsync(EntryId.Root, [file], options, null, CancellationToken.None);
        await reached.Task;

        try
        {
            Assert.True(session.IsBusy);

            await AssertBusyAsync(() => session.SaveAsync(SaveOptions.Default, null, CancellationToken.None), "Save");
            await AssertBusyAsync(() => session.VerifyAsync(null, CancellationToken.None), "Verify");
            await AssertBusyAsync(
                () => session.ExportAsync([existing], work.SubDirectory("out"), new ExportOptions(), null, CancellationToken.None),
                "Export");
            await AssertBusyAsync(() => session.OpenReadAsync(existing, CancellationToken.None), "OpenRead");
            await AssertBusyAsync(() => session.CreateFolderAsync(EntryId.Root, "nope", CancellationToken.None), "CreateFolder");
            await AssertBusyAsync(() => session.RenameAsync(existing, "other.bin", CancellationToken.None), "Rename");
            await AssertBusyAsync(() => session.DeleteAsync([existing], CancellationToken.None), "Delete");
            await AssertBusyAsync(() => session.UndoAsync(CancellationToken.None), "Undo");
            await AssertBusyAsync(
                () => session.ImportAsync(EntryId.Root, [file], new ImportOptions(), null, CancellationToken.None),
                "Import");
            await AssertBusyAsync(() => session.DiscardChangesAsync(CancellationToken.None), "DiscardChanges");

            // Snapshot reads are explicitly allowed to run alongside (API.md rule 3).
            Assert.Single(session.GetChildren(EntryId.Root));
            Assert.Equal("\\clash.bin", session.FormatPath(existing));
            Assert.NotNull(session.Find(existing));
            Assert.True(session.ValidateName(EntryId.Root, "free name.bin").IsValid);
            Assert.Single(session.Search("clash", null, 10, CancellationToken.None));
        }
        finally
        {
            release.SetResult();
            await running;
        }

        Assert.False(session.IsBusy);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelling_an_export_deletes_the_partial_file_and_keeps_the_finished_ones()
    {
        using var work = new TempDirectory("cancel-export");
        string path = Path.Combine(work.Path, "export.bastion");
        string sources = work.SubDirectory("source");

        string small = Path.Combine(sources, "small.bin");
        string large = Path.Combine(sources, "large.bin");
        await File.WriteAllBytesAsync(small, "small"u8.ToArray());
        await File.WriteAllBytesAsync(large, Content(LargeFileLength));

        await using IVaultSession session = await CreateAsync(path, work);
        var options = new ImportOptions(PreserveTimestamps: false);
        await session.ImportAsync(EntryId.Root, [small], options, null, CancellationToken.None);
        await session.ImportAsync(EntryId.Root, [large], options, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.True(session.TryResolvePath("\\small.bin", out EntryId smallId));
        Assert.True(session.TryResolvePath("\\large.bin", out EntryId largeId));

        string destination = work.SubDirectory("out");
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.BytesDone >= 4L * 1024 * 1024)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ExportAsync([smallId, largeId], destination, new ExportOptions(), progress, cancellation.Token));

        // The file that was finished before the cancellation stays; the one in flight leaves nothing.
        Assert.Equal("small", await File.ReadAllTextAsync(Path.Combine(destination, "small.bin")));
        Assert.False(File.Exists(Path.Combine(destination, "large.bin")), "the interrupted file must not appear");
        Assert.Empty(Directory.GetFiles(destination, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(destination, "*.partial", SearchOption.AllDirectories));

        // The session is unharmed: a second, uncancelled export writes everything.
        ExportResult complete = await session.ExportAsync(
            [smallId, largeId], work.SubDirectory("out2"), new ExportOptions(), null, CancellationToken.None);

        Assert.Empty(complete.Issues);
        Assert.Equal(2, complete.FilesWritten);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Content(LargeFileLength))),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(work.Path, "out2", "large.bin")))));
    }

    [Fact]
    public async Task Cancelling_a_save_before_the_replace_leaves_the_original_file_byte_for_byte()
    {
        using var work = new TempDirectory("cancel-save");
        string path = Path.Combine(work.Path, "save.bastion");
        string sources = work.SubDirectory("source");
        string large = Path.Combine(sources, "large.bin");
        await File.WriteAllBytesAsync(large, Content(LargeFileLength));

        await using IVaultSession session = await CreateAsync(path, work);
        byte[] before = await File.ReadAllBytesAsync(path);
        ulong counter = session.Statistics.SaveCounter;

        await session.ImportAsync(
            EntryId.Root, [large], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.BytesDone >= 4L * 1024 * 1024 && report.IsCancellable)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.SaveAsync(SaveOptions.Default, progress, cancellation.Token));

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(before)),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.Empty(Directory.GetFiles(work.Path, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(work.Path, "*.bak-*"));
        Assert.True(session.IsDirty, "the pending import is still pending");
        Assert.Equal(counter, session.Statistics.SaveCounter);

        // Step 6 onwards is not cancellable, so the retry has to succeed with the same token cancelled.
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.False(session.IsDirty);
        Assert.Equal(counter + 1, session.Statistics.SaveCounter);
        Assert.True((await session.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    [Fact]
    public async Task A_cancelled_save_copy_leaves_no_file_at_the_new_path()
    {
        using var work = new TempDirectory("cancel-savecopy");
        string path = Path.Combine(work.Path, "source.bastion");
        string copy = Path.Combine(work.Path, "copy.bastion");
        string sources = work.SubDirectory("source");
        string large = Path.Combine(sources, "large.bin");
        await File.WriteAllBytesAsync(large, Content(LargeFileLength));

        await using IVaultSession session = await CreateAsync(path, work);
        await session.ImportAsync(
            EntryId.Root, [large], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.Operation == VaultOperation.SaveCopy && report.BytesDone >= 4L * 1024 * 1024)
            {
                cancellation.Cancel();
            }
        });

        using Passphrase other = Passphrase.FromString("a different pass phrase entirely");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.SaveCopyAsync(copy, other, null, GoldenVault.Kdf, SaveOptions.Default, progress, cancellation.Token));

        Assert.False(File.Exists(copy));
        Assert.Empty(Directory.GetFiles(work.Path, "*.tmp-*"));
        Assert.False(session.IsDirty, "SaveCopy does not touch the session");
        Assert.True((await session.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    /// <summary>Asserts that an operation is refused with <see cref="VaultErrorCode.Busy"/>.</summary>
    /// <param name="operation">The operation to attempt.</param>
    /// <param name="name">Name of the operation, for the assertion message.</param>
    private static async Task AssertBusyAsync(Func<Task> operation, string name)
    {
        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(operation);
        VaultAssert.Failure(error, VaultErrorCode.Busy, $"{name} while another operation runs");
    }

    /// <summary>Deterministic content of a given length.</summary>
    /// <param name="length">Number of bytes.</param>
    private static byte[] Content(int length)
    {
        byte[] content = new byte[length];
        new DeterministicRandomSource(808).Fill(content);
        return content;
    }

    /// <summary>Creates a vault and returns the open session.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="work">Scratch directory, used for the staging override.</param>
    private static async Task<IVaultSession> CreateAsync(string path, TempDirectory work)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await new VaultFactory(new DeterministicRandomSource(41), new FixedClock(GoldenVault.Epoch))
            .CreateAsync(path, password, null, GoldenVault.Kdf, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a vault of this class.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="seed">Seed of the randomness seam.</param>
    private static async Task<IVaultSession> OpenAsync(string path, ulong seed)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await new VaultFactory(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
