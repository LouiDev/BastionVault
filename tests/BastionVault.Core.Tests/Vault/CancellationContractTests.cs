namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The cancellation table of API.md, row by row: what a cancelled operation is allowed to leave behind,
/// and the promise that every entry point honours a token that is already cancelled before it starts.
/// </summary>
public sealed class CancellationContractTests
{
    /// <summary>Password of every vault in this class.</summary>
    private const string Password = TamperVault.Password;

    /// <summary>Large enough for several progress reports, small enough to stay fast.</summary>
    private const int LargeFileLength = 9 * 1024 * 1024;

    [Fact]
    public async Task Every_entry_point_honours_a_token_that_is_already_cancelled()
    {
        using var work = new TempDirectory("cancelled-token");
        string path = Path.Combine(work.Path, "token.bastion");
        string sources = work.SubDirectory("source");
        string file = Path.Combine(sources, "a.bin");
        await File.WriteAllBytesAsync(file, "content"u8.ToArray());

        await using IVaultSession session = await CreateAsync(path);
        await session.ImportAsync(
            EntryId.Root, [file], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);
        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "folder", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.True(session.TryResolvePath("\\a.bin", out EntryId entry));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        CancellationToken ct = cancellation.Token;

        using Passphrase password = Passphrase.FromString(Password);
        (string Name, Func<Task> Operation)[] rows =
        [
            ("CreateFolder", () => session.CreateFolderAsync(EntryId.Root, "cancelled", ct)),
            ("Rename", () => session.RenameAsync(entry, "renamed.bin", ct)),
            ("SetComment", () => session.SetCommentAsync(entry, "cancelled", ct)),
            ("Move", () => session.MoveAsync([entry], folder, ct)),
            ("Copy", () => session.CopyAsync([entry], folder, ct)),
            ("Delete", () => session.DeleteAsync([entry], ct)),
            ("Undo", () => session.UndoAsync(ct)),
            ("Redo", () => session.RedoAsync(ct)),
            ("Import", () => session.ImportAsync(EntryId.Root, [file], new ImportOptions(), null, ct)),
            ("Export", () => session.ExportAsync([entry], work.SubDirectory("out"), new ExportOptions(), null, ct)),
            ("OpenRead", () => session.OpenReadAsync(entry, ct)),
            ("Verify", () => session.VerifyAsync(null, ct)),
            ("Recover", () => session.RecoverAsync(work.SubDirectory("recover"), new ExportOptions(), null, ct)),
            ("Save", () => session.SaveAsync(SaveOptions.Default, null, ct)),
            ("SaveCopy", () => session.SaveCopyAsync(
                Path.Combine(work.Path, "copy.bastion"), password, null, GoldenVault.Kdf, SaveOptions.Default, null, ct)),
            ("ChangeCredentials", () => session.ChangeCredentialsAsync(
                password, null, GoldenVault.Kdf, CredentialChangeMode.RewrapOnly, null, ct)),
            ("DiscardChanges", () => session.DiscardChangesAsync(ct)),
            ("ReadHeader", () => Factory(51).ReadHeaderAsync(path, ct)),
            ("Open", () => Factory(52).OpenAsync(path, password, null, OpenOptions.Default, null, ct)),
            ("Create", () => Factory(53).CreateAsync(
                Path.Combine(work.Path, "created.bastion"), password, null, GoldenVault.Kdf, null, ct)),
        ];

        foreach ((string name, Func<Task> operation) in rows)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
            Assert.False(session.IsBusy, $"{name} did not release the session lock");
        }

        // Nothing above changed the vault, on disk or in memory.
        Assert.False(session.IsDirty);
        Assert.Equal("a.bin", session.Find(entry)!.Name);
        Assert.Equal(EntryId.Root, session.Find(entry)!.ParentId);
        Assert.Empty(session.GetChildren(folder));
        Assert.False(session.Pending.CredentialChangePending);
        Assert.DoesNotContain(Directory.GetFiles(work.Path, "*.bastion"), candidate => candidate != path);
        Assert.Empty(Directory.GetFiles(work.Path, "*.tmp-*"));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(work.Path, "out")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(work.Path, "recover")));
    }

    [Fact]
    public async Task Creating_a_vault_that_is_cancelled_during_the_key_derivation_leaves_no_file()
    {
        using var work = new TempDirectory("cancel-create");
        string path = Path.Combine(work.Path, "never.bastion");

        using var cancellation = new CancellationTokenSource();
        var reports = new List<VaultProgress>();
        var progress = new SynchronousProgress(report =>
        {
            reports.Add(report);
            cancellation.Cancel();
        });

        using Passphrase password = Passphrase.FromString(Password);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Factory(54).CreateAsync(path, password, null, GoldenVault.Kdf, progress, cancellation.Token));

        // The KDF itself cannot be interrupted; the token is honoured as soon as it returns.
        Assert.NotEmpty(reports);
        Assert.False(reports[0].IsCancellable, "the key derivation phase must report that it cannot be cancelled");
        Assert.Equal(VaultOperation.Create, reports[0].Operation);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(work.Path));
    }

    [Fact]
    public async Task Opening_a_vault_reports_that_the_key_derivation_cannot_be_cancelled()
    {
        using var work = new TempDirectory("open-progress");
        string path = Path.Combine(work.Path, "open.bastion");
        await using (IVaultSession created = await CreateAsync(path))
        {
        }

        var reports = new List<VaultProgress>();
        var progress = new SynchronousProgress(reports.Add);

        using Passphrase password = Passphrase.FromString(Password);
        await using IVaultSession session = await Factory(55)
            .OpenAsync(path, password, null, OpenOptions.Default, progress, CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Contains(reports, report => report.Operation == VaultOperation.Open && !report.IsCancellable);
        Assert.Equal(VaultOperation.Open, reports[^1].Operation);
    }

    [Fact]
    public async Task A_cancelled_verify_throws_and_leaves_the_session_usable()
    {
        using var work = new TempDirectory("cancel-verify");
        await using IVaultSession session = await WithLargeFileAsync(work);

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.BytesDone >= 4L * 1024 * 1024)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.VerifyAsync(progress, cancellation.Token));

        Assert.False(session.IsBusy);
        VerifyReport report = await session.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
        Assert.Equal(1, report.FilesChecked);
    }

    [Fact]
    public async Task A_cancelled_recover_deletes_its_partial_output()
    {
        using var work = new TempDirectory("cancel-recover");
        await using IVaultSession session = await WithLargeFileAsync(work);
        string destination = work.SubDirectory("recovered");

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.BytesDone >= 4L * 1024 * 1024)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RecoverAsync(destination, new ExportOptions(WritePartialFiles: true), progress, cancellation.Token));

        Assert.Empty(Directory.GetFiles(destination, "*.tmp-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(destination, "*.partial", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(destination, "large.bin")));
    }

    [Fact]
    public async Task A_cancelled_credential_change_is_not_applied()
    {
        using var work = new TempDirectory("cancel-credentials");
        string path = Path.Combine(work.Path, "credentials.bastion");
        await using IVaultSession session = await CreateAsync(path);

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress(_ => cancellation.Cancel());

        using (Passphrase other = Passphrase.FromString("a different pass phrase entirely"))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.ChangeCredentialsAsync(
                    other, null, GoldenVault.Kdf, CredentialChangeMode.Rekey, progress, cancellation.Token));
        }

        Assert.False(session.Pending.CredentialChangePending);
        Assert.False(session.Pending.RekeyPending);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);

        // The old password still works after the save that never learned about the new one.
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        await using IVaultSession reopened = await OpenAsync(path);
        Assert.Equal(2ul, reopened.Statistics.SaveCounter);
    }

    [Fact]
    public async Task A_cancelled_unlock_leaves_the_session_locked()
    {
        using var work = new TempDirectory("cancel-unlock");
        string path = Path.Combine(work.Path, "locked.bastion");
        await using IVaultSession session = await CreateAsync(path);

        await session.CreateFolderAsync(EntryId.Root, "kept across the lock", CancellationToken.None);
        session.Lock();
        Assert.True(session.IsLocked);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        using Passphrase password = Passphrase.FromString(Password);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.UnlockAsync(password, null, null, cancellation.Token));

        Assert.True(session.IsLocked, "a cancelled unlock must not unlock the session");
        Assert.Single(session.GetChildren(EntryId.Root));

        await session.UnlockAsync(password, null, null, CancellationToken.None);
        Assert.False(session.IsLocked);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
    }

    /// <summary>A factory with a seeded randomness seam and the fixed clock.</summary>
    /// <param name="seed">Seed of the randomness seam.</param>
    private static VaultFactory Factory(ulong seed) =>
        new(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch));

    /// <summary>Creates a vault and returns the open session.</summary>
    /// <param name="path">Path of the vault.</param>
    private static async Task<IVaultSession> CreateAsync(string path)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await Factory(56)
            .CreateAsync(path, password, null, GoldenVault.Kdf, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a vault of this class.</summary>
    /// <param name="path">Path of the vault.</param>
    private static async Task<IVaultSession> OpenAsync(string path)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await Factory(57)
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Creates a saved vault holding one file that is long enough to report progress.</summary>
    /// <param name="work">Scratch directory.</param>
    private static async Task<IVaultSession> WithLargeFileAsync(TempDirectory work)
    {
        string path = Path.Combine(work.Path, "large.bastion");
        string source = Path.Combine(work.SubDirectory("source"), "large.bin");

        byte[] content = new byte[LargeFileLength];
        new DeterministicRandomSource(909).Fill(content);
        await File.WriteAllBytesAsync(source, content).ConfigureAwait(false);

        IVaultSession session = await CreateAsync(path).ConfigureAwait(false);
        try
        {
            await session.ImportAsync(
                    EntryId.Root, [source], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None)
                .ConfigureAwait(false);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
