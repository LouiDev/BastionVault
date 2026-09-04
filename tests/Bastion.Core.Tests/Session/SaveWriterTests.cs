using Bastion.Core.Session;
using Bastion.Core.Tests.Vault;

namespace Bastion.Core.Tests.Session;

/// <summary>
/// The save state machine seen from the inside: the rules that no public call can reach but that keep
/// re-keying correct by construction (FORMAT.md section 8.3 step 3).
/// </summary>
public sealed class SaveWriterTests
{
    [Fact]
    public async Task A_verbatim_blob_copy_into_a_different_key_space_is_refused()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.ImportAsync(
            EntryId.Root,
            [context.WriteSourceFile("payload.bin", VaultTestContext.Bytes(2048, 71))],
            new ImportOptions(),
            null,
            CancellationToken.None);

        var inner = (VaultSession)session;
        using VaultCrypto foreign = VaultCrypto.Create(new DeterministicRandomSource(99));
        using Bastion.Core.Crypto.KeyMaterial kek = Bastion.Core.Crypto.KeyMaterial.Random(32, new DeterministicRandomSource(98));

        var request = new SaveRequest
        {
            DestinationPath = Path.Combine(context.Root, "never-written.bastion"),
            ReplaceExisting = false,
            Entries = [.. inner.Tree.CanonicalOrder()],
            NextEntryId = inner.Tree.NextEntryId,
            SourceCrypto = inner.RequireCrypto(),
            DestinationCrypto = foreign,

            // Compact means "copy the blobs verbatim", which cannot be right under a different vault key.
            Mode = SaveMode.Compact,
            Wrap = new WrapPlan { Kdf = VaultTestContext.FastKdf, KdfSalt = new byte[32], Kek = kek },
            SaveCounter = 2,
            SavedUtc = context.Clock.UtcNow,
            SizeObfuscation = false,
            Operation = VaultOperation.Save,
        };

        var writer = new SaveWriter(new DeterministicRandomSource(97), new DefaultVaultPaths(new DeterministicRandomSource(96)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.RunAsync(request, null, CancellationToken.None));

        Assert.False(File.Exists(request.DestinationPath));
        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));
    }

    [Fact]
    public async Task A_cancelled_save_leaves_the_vault_and_the_session_untouched()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.ImportAsync(
            EntryId.Root,
            [context.WriteSourceFile("payload.bin", VaultTestContext.Bytes(400_000, 73))],
            new ImportOptions(),
            null,
            CancellationToken.None);

        byte[] before = File.ReadAllBytes(context.VaultPath);

        using var cancellation = new CancellationTokenSource();
        // A Progress<T> callback is posted to the thread pool, so under load it can land after the save
        // has already committed; SynchronousProgress cancels inline on the reporting thread.
        var progress = new SynchronousProgress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.SaveAsync(SaveOptions.Default, progress, cancellation.Token));

        Assert.Equal(
            VaultTestContext.Digest(before),
            VaultTestContext.Digest(File.ReadAllBytes(context.VaultPath)));
        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));
        Assert.True(session.IsDirty);
        Assert.Equal(1ul, session.Statistics.SaveCounter);

        // The session still works: a second attempt commits everything.
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.False(session.IsDirty);
        Assert.Equal(2ul, session.Statistics.SaveCounter);
    }

    [Fact]
    public async Task A_rekeying_save_leaves_no_temporary_or_backup_behind()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.ImportAsync(
            EntryId.Root,
            [context.WriteSourceFile("payload.bin", VaultTestContext.Bytes(70_000, 72))],
            new ImportOptions(),
            null,
            CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        using (Passphrase next = Passphrase.FromString(VaultTestContext.OtherPassword))
        {
            await session.ChangeCredentialsAsync(
                next, null, VaultTestContext.FastKdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
        }

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(context.Root, "*.tmp-*"));
        Assert.Empty(Directory.GetFiles(context.Root, "*.bak-*"));
        Assert.Empty(Directory.GetFiles(context.Root, "*~stage-*"));
        Assert.Equal(3ul, session.Statistics.SaveCounter);
    }
}
