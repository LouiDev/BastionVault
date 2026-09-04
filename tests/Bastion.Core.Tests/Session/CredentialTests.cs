namespace Bastion.Core.Tests.Session;

/// <summary>Password and keyfile changes, saving a copy, and locking a session.</summary>
public sealed class CredentialTests
{
    [Fact]
    public async Task Changing_the_password_by_rekeying_replaces_every_key()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(200_000, 51);
        byte[] vaultBefore;
        EntryId file;

        await using (IVaultSession session = await context.CreateAsync())
        {
            string source = context.WriteSourceFile("secret.bin", content);
            ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
            file = imported.Imported.Single();
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            vaultBefore = File.ReadAllBytes(context.VaultPath);

            using Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword);
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);

            Assert.True(session.IsDirty);
            Assert.True(session.Pending.CredentialChangePending);
            Assert.True(session.Pending.RekeyPending);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            Assert.False(session.Pending.CredentialChangePending);
        }

        byte[] vaultAfter = File.ReadAllBytes(context.VaultPath);
        Assert.Equal(vaultBefore.Length, vaultAfter.Length);
        Assert.NotEqual(VaultTestContext.Digest(vaultBefore), VaultTestContext.Digest(vaultAfter));

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        await using IVaultSession reopened = await context.OpenAsync(VaultTestContext.OtherPassword);
        Assert.Equal(VaultTestContext.Digest(content), VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(reopened, file)));
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    [Fact]
    public async Task A_second_save_after_a_credential_change_still_produces_an_openable_vault()
    {
        // Regression: the pending credential change owns its salt buffer and zeroes it once the save has
        // been adopted. When the session's header aliased that buffer, the next save wrote a zeroed salt
        // into the header while the wrapped key stayed as it was, and the vault could never be opened again.
        using var context = new VaultTestContext();
        await using (IVaultSession session = await context.CreateAsync())
        {
            using (Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword))
            {
                await session.ChangeCredentialsAsync(
                    next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
            }

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            await session.CreateFolderAsync(EntryId.Root, "Later", CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await using IVaultSession reopened = await context.OpenAsync(VaultTestContext.OtherPassword);
        Assert.Equal("Later", Assert.Single(reopened.GetChildren(EntryId.Root)).Name);
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    [Fact]
    public async Task Changing_the_password_by_rewrapping_keeps_the_data_section_untouched()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(120_000, 52);
        EntryId file;
        long dataOffsetBefore;
        byte[] dataBefore;

        await using (IVaultSession session = await context.CreateAsync())
        {
            string source = context.WriteSourceFile("payload.bin", content);
            ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
            file = imported.Imported.Single();
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            VaultHeaderInfo info = await context.NewFactory().ReadHeaderAsync(context.VaultPath, CancellationToken.None);
            dataOffsetBefore = 160 + info.IndexLength;
            dataBefore = ReadRange(context.VaultPath, dataOffsetBefore, content.Length);

            using Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword);
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.RewrapOnly, null, CancellationToken.None);

            Assert.True(session.Pending.CredentialChangePending);
            Assert.False(session.Pending.RekeyPending);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        byte[] dataAfter = ReadRange(context.VaultPath, dataOffsetBefore, content.Length);
        Assert.Equal(VaultTestContext.Digest(dataBefore), VaultTestContext.Digest(dataAfter));

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        await using IVaultSession reopened = await context.OpenAsync(VaultTestContext.OtherPassword);
        Assert.Equal(VaultTestContext.Digest(content), VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(reopened, file)));
    }

    [Fact]
    public async Task A_keyfile_can_be_added_and_removed()
    {
        using var context = new VaultTestContext();
        using KeyFile keyFile = KeyFile.FromBytes(VaultTestContext.Bytes(64, 53));

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);

            using Passphrase same = Passphrase.FromString(VaultTestContext.Password);
            await session.ChangeCredentialsAsync(
                same, keyFile, VaultTestContext.FastKdf, CredentialChangeMode.RewrapOnly, null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenAsync();
        });

        await using (IVaultSession withKeyFile = await context.OpenAsync(keyFile: keyFile))
        {
            Assert.Single(withKeyFile.GetChildren(EntryId.Root));

            using Passphrase same = Passphrase.FromString(VaultTestContext.Password);
            await withKeyFile.ChangeCredentialsAsync(
                same, null, VaultTestContext.FastKdf, CredentialChangeMode.RewrapOnly, null, CancellationToken.None);
            await withKeyFile.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await using IVaultSession withoutKeyFile = await context.OpenAsync();
        Assert.Single(withoutKeyFile.GetChildren(EntryId.Root));
    }

    [Fact]
    public async Task Saving_a_copy_leaves_the_original_alone()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(90_000, 54);
        string copyPath = Path.Combine(context.Root, "copy.bastion");
        EntryId file;

        await using (IVaultSession session = await context.CreateAsync())
        {
            string source = context.WriteSourceFile("shared.bin", content);
            ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
            file = imported.Imported.Single();
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            using Passphrase other = Passphrase.FromString(VaultTestContext.OtherPassword);
            await session.SaveCopyAsync(
                copyPath, other, null, VaultTestContext.FastKdf, SaveOptions.Default, null, CancellationToken.None);

            Assert.False(session.IsDirty);
            Assert.Equal(2ul, session.Statistics.SaveCounter);
        }

        await using (IVaultSession original = await context.OpenAsync())
        {
            Assert.Equal(VaultTestContext.Digest(content), VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(original, file)));
        }

        await using IVaultSession copy = await context.OpenOtherAsync(copyPath, VaultTestContext.OtherPassword);
        Assert.Equal(VaultTestContext.Digest(content), VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(copy, file)));
        Assert.True((await copy.VerifyAsync(null, CancellationToken.None)).IsClean);

        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession _ = await context.OpenOtherAsync(copyPath);
        });
    }

    [Fact]
    public async Task Saving_a_copy_over_an_existing_file_is_refused()
    {
        using var context = new VaultTestContext();
        string copyPath = Path.Combine(context.Root, "copy.bastion");
        File.WriteAllBytes(copyPath, [1, 2, 3]);

        await using IVaultSession session = await context.CreateAsync();
        using Passphrase other = Passphrase.FromString(VaultTestContext.OtherPassword);

        VaultIoException error = await Assert.ThrowsAsync<VaultIoException>(
            () => session.SaveCopyAsync(copyPath, other, null, VaultTestContext.FastKdf, SaveOptions.Default, null, CancellationToken.None));
        Assert.Equal(VaultErrorCode.IoError, error.Code);
    }

    [Fact]
    public async Task Locking_keeps_the_tree_and_refuses_work_until_it_is_unlocked()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        string source = context.WriteSourceFile("staged.bin", VaultTestContext.Bytes(5000, 55));
        ImportResult imported = await session.ImportAsync(folder, [source], new ImportOptions(), null, CancellationToken.None);

        session.Lock();
        session.Lock();

        Assert.True(session.IsLocked);
        Assert.True(session.IsDirty);
        Assert.Equal(2, session.GetChildren(EntryId.Root).Count + session.GetChildren(folder).Count);

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.CreateFolderAsync(EntryId.Root, "Later", CancellationToken.None));
        Assert.Equal(VaultErrorCode.SessionLocked, error.Code);

        await Assert.ThrowsAsync<VaultOperationException>(
            () => session.SaveAsync(SaveOptions.Default, null, CancellationToken.None));

        using (Passphrase wrong = Passphrase.FromString(VaultTestContext.OtherPassword))
        {
            await Assert.ThrowsAsync<VaultAuthenticationException>(
                () => session.UnlockAsync(wrong, null, null, CancellationToken.None));
        }

        using (Passphrase right = Passphrase.FromString(VaultTestContext.Password))
        {
            await session.UnlockAsync(right, null, null, CancellationToken.None);
        }

        Assert.False(session.IsLocked);
        Assert.Equal(
            VaultTestContext.Digest(VaultTestContext.Bytes(5000, 55)),
            VaultTestContext.Digest(await VaultTestContext.ReadAllAsync(session, imported.Imported.Single())));

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Locking_discards_a_pending_credential_change()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        using (Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword))
        {
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
        }

        Assert.True(session.Pending.CredentialChangePending);

        session.ZeroKeys();
        Assert.False(session.Pending.CredentialChangePending);

        using (Passphrase right = Passphrase.FromString(VaultTestContext.Password))
        {
            await session.UnlockAsync(right, null, null, CancellationToken.None);
        }

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal(2ul, reopened.Statistics.SaveCounter);
    }

    [Fact]
    public async Task A_credential_change_can_be_undone()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        using (Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword))
        {
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
        }

        Assert.True(session.Pending.CredentialChangePending);
        await session.UndoAsync(CancellationToken.None);
        Assert.False(session.Pending.CredentialChangePending);

        await session.RedoAsync(CancellationToken.None);
        Assert.True(session.Pending.CredentialChangePending);
    }

    /// <summary>
    /// FORMAT.md section 2.4: the derived vault id is "the key of local per-machine records", and
    /// <see cref="IVaultSession.VaultIdHex"/> is how a host gets at it. It is 32 lowercase hex
    /// characters, it is the same value the crypto layer derived, it survives a lock (it is a label
    /// expansion, not key material, so a locked session can still be identified), and it is the same
    /// after closing and reopening the file with the same password.
    /// </summary>
    [Fact]
    public async Task The_vault_id_identifies_the_key_space_and_survives_a_lock_and_a_reopen()
    {
        using var context = new VaultTestContext();
        string identity;

        await using (IVaultSession session = await context.CreateAsync())
        {
            identity = session.VaultIdHex;

            Assert.Equal(32, identity.Length);
            Assert.All(identity, c => Assert.Contains(c, "0123456789abcdef"));

            // It is exactly what Crypto derives from the vault key, not a second definition of it.
            var real = (Bastion.Core.Session.VaultSession)session;
            byte[] derived = Bastion.Core.Crypto.VaultKeys.DeriveVaultId(real.RequireCrypto().VaultKey.Span);
            Assert.Equal(Convert.ToHexStringLower(derived), identity);
            Assert.Equal(Convert.ToHexStringLower(real.RequireCrypto().VaultId), identity);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            Assert.Equal(identity, session.VaultIdHex);

            session.Lock();
            Assert.True(session.IsLocked);
            Assert.Equal(identity, session.VaultIdHex);
        }

        await using (IVaultSession reopened = await context.OpenAsync())
        {
            Assert.Equal(identity, reopened.VaultIdHex);
        }
    }

    /// <summary>
    /// The identity is per key space, so a re-key rotates it — but only once the save has committed
    /// it, because until then the file on disk is still wrapped with the old key.
    /// </summary>
    [Fact]
    public async Task The_vault_id_changes_after_a_rekey_is_saved_and_not_before()
    {
        using var context = new VaultTestContext();
        string before;
        string after;

        await using (IVaultSession session = await context.CreateAsync())
        {
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            before = session.VaultIdHex;

            using Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword);
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);

            Assert.Equal(before, session.VaultIdHex);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            after = session.VaultIdHex;
        }

        Assert.NotEqual(before, after);
        Assert.Equal(32, after.Length);

        await using IVaultSession reopened = await context.OpenAsync(VaultTestContext.OtherPassword);
        Assert.Equal(after, reopened.VaultIdHex);
    }

    /// <summary>A rewrap keeps the vault key, so it keeps the identity the records are filed under.</summary>
    [Fact]
    public async Task A_rewrap_keeps_the_vault_id()
    {
        using var context = new VaultTestContext();

        await using IVaultSession session = await context.CreateAsync();
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        string before = session.VaultIdHex;

        using Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword);
        await session.ChangeCredentialsAsync(
            next, null, VaultTestContext.FastKdf, CredentialChangeMode.RewrapOnly, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.Equal(before, session.VaultIdHex);
    }

    /// <summary>Reads a byte range straight out of a file.</summary>
    /// <param name="path">File to read.</param>
    /// <param name="offset">First byte.</param>
    /// <param name="length">Number of bytes.</param>
    private static byte[] ReadRange(string path, long offset, int length)
    {
        using FileStream stream = File.OpenRead(path);
        stream.Position = offset;
        byte[] buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
