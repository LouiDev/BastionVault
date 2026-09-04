namespace Bastion.Core.Tests.Session;

/// <summary>
/// <see cref="IVaultSession.VerifyPasswordAsync"/>: the credential check the Change-credentials dialog
/// uses. It answers a question instead of raising one, and it must leave the session exactly as it was.
/// </summary>
public sealed class VerifyPasswordTests
{
    [Fact]
    public async Task The_current_password_verifies_and_nothing_about_the_session_changes()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        string source = context.WriteSourceFile("notes.txt", "hello"u8.ToArray());
        await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        await session.CreateFolderAsync(EntryId.Root, "Docs", CancellationToken.None);

        bool lockedBefore = session.IsLocked;
        bool dirtyBefore = session.IsDirty;
        bool canUndoBefore = session.CanUndo;
        string? undoBefore = session.UndoDescription;
        VaultStatistics statisticsBefore = session.Statistics;
        PendingChanges pendingBefore = session.Pending;
        int childrenBefore = session.GetChildren(EntryId.Root).Count;
        byte[] fileBefore = File.ReadAllBytes(context.VaultPath);

        using Passphrase correct = Passphrase.FromString(VaultTestContext.Password);
        Assert.True(await session.VerifyPasswordAsync(correct, null, CancellationToken.None));

        Assert.Equal(lockedBefore, session.IsLocked);
        Assert.Equal(dirtyBefore, session.IsDirty);
        Assert.Equal(canUndoBefore, session.CanUndo);
        Assert.Equal(undoBefore, session.UndoDescription);
        Assert.Equal(statisticsBefore, session.Statistics);
        Assert.Equal(pendingBefore, session.Pending);
        Assert.Equal(childrenBefore, session.GetChildren(EntryId.Root).Count);
        Assert.Equal(VaultTestContext.Digest(fileBefore), VaultTestContext.Digest(File.ReadAllBytes(context.VaultPath)));

        // Still fully usable afterwards.
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task A_wrong_password_returns_false_instead_of_throwing()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        using Passphrase wrong = Passphrase.FromString(VaultTestContext.OtherPassword);
        Assert.False(await session.VerifyPasswordAsync(wrong, null, CancellationToken.None));
        Assert.False(session.IsLocked);
        Assert.False(session.IsDirty);

        using Passphrase almost = Passphrase.FromString(VaultTestContext.Password + " ");
        Assert.False(await session.VerifyPasswordAsync(almost, null, CancellationToken.None));
    }

    [Fact]
    public async Task The_keyfile_is_part_of_the_answer()
    {
        using var context = new VaultTestContext();
        string keyfilePath = context.WriteSourceFile("vault.key", KeyFile.GenerateContent(64, new DeterministicRandomSource(9)));
        using KeyFile keyFile = KeyFile.Load(keyfilePath);

        await using IVaultSession session = await context.CreateAsync(keyFile: keyFile);

        using Passphrase correct = Passphrase.FromString(VaultTestContext.Password);
        Assert.True(await session.VerifyPasswordAsync(correct, keyFile, CancellationToken.None));
        Assert.False(await session.VerifyPasswordAsync(correct, null, CancellationToken.None));

        string otherPath = context.WriteSourceFile("other.key", KeyFile.GenerateContent(64, new DeterministicRandomSource(10)));
        using KeyFile other = KeyFile.Load(otherPath);
        Assert.False(await session.VerifyPasswordAsync(correct, other, CancellationToken.None));
    }

    [Fact]
    public async Task A_locked_session_can_be_asked_and_stays_locked()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();
        session.Lock();

        using Passphrase correct = Passphrase.FromString(VaultTestContext.Password);
        Assert.True(await session.VerifyPasswordAsync(correct, null, CancellationToken.None));
        Assert.True(session.IsLocked);

        using Passphrase wrong = Passphrase.FromString(VaultTestContext.OtherPassword);
        Assert.False(await session.VerifyPasswordAsync(wrong, null, CancellationToken.None));
        Assert.True(session.IsLocked);
    }

    [Fact]
    public async Task A_pending_credential_change_is_ignored_until_it_is_saved()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        using (Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword))
        {
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
        }

        using Passphrase current = Passphrase.FromString(VaultTestContext.Password);
        using Passphrase future = Passphrase.FromString(VaultTestContext.OtherPassword);

        // The header on disk is what "current" means.
        Assert.True(await session.VerifyPasswordAsync(current, null, CancellationToken.None));
        Assert.False(await session.VerifyPasswordAsync(future, null, CancellationToken.None));
        Assert.True(session.Pending.CredentialChangePending);

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.False(await session.VerifyPasswordAsync(current, null, CancellationToken.None));
        Assert.True(await session.VerifyPasswordAsync(future, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured_and_a_null_password_is_caller_error()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using Passphrase correct = Passphrase.FromString(VaultTestContext.Password);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.VerifyPasswordAsync(correct, null, cancelled.Token));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.VerifyPasswordAsync(null!, null, CancellationToken.None));

        Assert.False(session.IsLocked);
    }
}
