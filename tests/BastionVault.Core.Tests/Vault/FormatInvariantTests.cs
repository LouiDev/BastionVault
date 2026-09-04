using System.Security.Cryptography;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The MUST rules of FORMAT.md that are invisible in a single file and only show up when a vault is
/// written more than once: the nonce-safety invariants of sections 2.5 and 2.7, the stability of the key
/// wrap across ordinary saves (section 2.6), and the promise of section 8.4 that a copy never shares a
/// key space with its original.
/// </summary>
public sealed class FormatInvariantTests
{
    /// <summary>Password of every vault in this class.</summary>
    private const string Password = TamperVault.Password;

    /// <summary>The second password, for credential changes.</summary>
    private const string OtherPassword = "a different pass phrase entirely";

    [Fact]
    public async Task Every_credential_change_writes_a_fresh_salt_and_wrap_nonce()
    {
        using var work = new TempDirectory("wrap-freshness");
        string path = Path.Combine(work.Path, "wrap.bastion");

        var salts = new HashSet<string>(StringComparer.Ordinal);
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        await using IVaultSession session = await CreateAsync(path, 61);
        Record(path, salts, nonces, pairs);

        // An ordinary save keeps the wrap: FORMAT.md section 2.6 relies on it being stable so the
        // wrapped key can be copied verbatim.
        (byte[] salt, byte[] nonce, byte[] wrapped) = WrapOf(path);
        await session.CreateFolderAsync(EntryId.Root, "one", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        (byte[] salt2, byte[] nonce2, byte[] wrapped2) = WrapOf(path);

        Assert.Equal(Convert.ToHexString(salt), Convert.ToHexString(salt2));
        Assert.Equal(Convert.ToHexString(nonce), Convert.ToHexString(nonce2));
        Assert.Equal(Convert.ToHexString(wrapped), Convert.ToHexString(wrapped2));

        // Every credential change, whatever its mode, must produce a fresh pair.
        for (int round = 0; round < 4; round++)
        {
            CredentialChangeMode mode = round % 2 == 0 ? CredentialChangeMode.Rekey : CredentialChangeMode.RewrapOnly;
            string password = round % 2 == 0 ? OtherPassword : Password;

            using (Passphrase next = Passphrase.FromString(password))
            {
                await session.ChangeCredentialsAsync(next, null, GoldenVault.Kdf, mode, null, CancellationToken.None);
            }

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            Record(path, salts, nonces, pairs);
        }

        Assert.Equal(5, salts.Count);
        Assert.Equal(5, nonces.Count);
        Assert.Equal(5, pairs.Count);
    }

    [Fact]
    public async Task No_blob_id_is_ever_used_twice_and_a_rekey_replaces_all_of_them()
    {
        using var work = new TempDirectory("blob-freshness");
        string path = Path.Combine(work.Path, "blobs.bastion");
        string sources = work.SubDirectory("source");

        string first = Path.Combine(sources, "first.bin");
        string second = Path.Combine(sources, "second.bin");
        await File.WriteAllBytesAsync(first, Content(100_000, 1));
        await File.WriteAllBytesAsync(second, Content(70_000, 2));

        var everSeen = new HashSet<string>(StringComparer.Ordinal);
        var options = new ImportOptions(PreserveTimestamps: false);

        await using IVaultSession session = await CreateAsync(path, 62);

        await session.ImportAsync(EntryId.Root, [first], options, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Dictionary<string, string> afterFirst = BlobIds(path, Password);
        AddAll(everSeen, afterFirst);

        await session.ImportAsync(EntryId.Root, [second], options, null, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Dictionary<string, string> afterSecond = BlobIds(path, Password);

        // The stored blob was copied verbatim, so its id stays; the new content got a fresh one.
        Assert.Equal(afterFirst["first.bin"], afterSecond["first.bin"]);
        Assert.DoesNotContain(afterSecond["second.bin"], everSeen);
        AddAll(everSeen, afterSecond);

        // An in-vault copy is a content write: it must not share the blob of its original.
        Assert.True(session.TryResolvePath("\\first.bin", out EntryId original));
        await session.CopyAsync([original], EntryId.Root, CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Dictionary<string, string> afterCopy = BlobIds(path, Password);

        Assert.Equal(3, afterCopy.Count);
        Assert.Equal(3, afterCopy.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (string id in afterCopy.Values.Where(id => !everSeen.Contains(id)))
        {
            everSeen.Add(id);
        }

        using (Passphrase next = Passphrase.FromString(OtherPassword))
        {
            await session.ChangeCredentialsAsync(
                next, null, GoldenVault.Kdf, CredentialChangeMode.Rekey, null, CancellationToken.None);
        }

        long lengthBefore = new FileInfo(path).Length;
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Dictionary<string, string> afterRekey = BlobIds(path, OtherPassword);

        // Section 8.4: every blob is re-encrypted under a fresh id, and the lengths do not move.
        Assert.Equal(lengthBefore, new FileInfo(path).Length);
        foreach ((string name, string id) in afterRekey)
        {
            Assert.DoesNotContain(id, everSeen);
            Assert.NotEqual(afterCopy[name], id);
        }

        Assert.Equal(afterRekey.Count, afterRekey.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_header_says_nothing_about_whether_a_keyfile_is_needed()
    {
        // Two vaults built from the same seeds and the same password, one with a keyfile: everything the
        // header carries has to be identical except the wrapped key itself (THREAT-MODEL.md A1).
        using var work = new TempDirectory("keyfile-privacy");
        string withoutKeyFile = Path.Combine(work.Path, "plain.bastion");
        string withKeyFile = Path.Combine(work.Path, "keyed.bastion");

        using KeyFile keyFile = KeyFile.FromBytes(Content(64, 3));
        using (Passphrase password = Passphrase.FromString(Password))
        {
            await using (IVaultSession plain = await new VaultFactory(
                new DeterministicRandomSource(63), new FixedClock(GoldenVault.Epoch))
                .CreateAsync(withoutKeyFile, password, null, GoldenVault.Kdf, null, CancellationToken.None))
            {
            }

            await using (IVaultSession keyed = await new VaultFactory(
                new DeterministicRandomSource(63), new FixedClock(GoldenVault.Epoch))
                .CreateAsync(withKeyFile, password, keyFile, GoldenVault.Kdf, null, CancellationToken.None))
            {
            }
        }

        byte[] plainBytes = await File.ReadAllBytesAsync(withoutKeyFile);
        byte[] keyedBytes = await File.ReadAllBytesAsync(withKeyFile);

        Assert.Equal(plainBytes.Length, keyedBytes.Length);
        for (int offset = 0; offset < VaultHeader.Size; offset++)
        {
            bool insideWrappedKey = offset is >= 76 and < 124;
            bool equal = plainBytes[offset] == keyedBytes[offset];
            Assert.True(
                equal != insideWrappedKey,
                $"header byte {offset}: the two vaults must differ only inside the wrapped key (equal={equal})");
        }

        // And the keyed vault really does need its keyfile.
        using Passphrase again = Passphrase.FromString(Password);
        await Assert.ThrowsAsync<VaultAuthenticationException>(async () =>
        {
            await using IVaultSession refused = await new VaultFactory(
                    new DeterministicRandomSource(64), new FixedClock(GoldenVault.Epoch))
                .OpenAsync(withKeyFile, again, null, OpenOptions.Default, null, CancellationToken.None);
        });
    }

    [Fact]
    public async Task A_saved_copy_shares_no_key_material_with_its_original()
    {
        using var work = new TempDirectory("copy-key-space");
        string path = Path.Combine(work.Path, "original.bastion");
        string copy = Path.Combine(work.Path, "copy.bastion");
        string source = Path.Combine(work.SubDirectory("source"), "payload.bin");
        byte[] content = Content(60_000, 4);
        await File.WriteAllBytesAsync(source, content);

        await using (IVaultSession session = await CreateAsync(path, 65))
        {
            await session.ImportAsync(
                EntryId.Root, [source], new ImportOptions(PreserveTimestamps: false), null, CancellationToken.None);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            using Passphrase other = Passphrase.FromString(OtherPassword);
            await session.SaveCopyAsync(
                copy, other, null, GoldenVault.Kdf, SaveOptions.Default, null, CancellationToken.None);
        }

        using VaultImage original = VaultImage.Load(path, Password);
        using VaultImage duplicate = VaultImage.Load(copy, OtherPassword);

        Assert.NotEqual(Convert.ToHexString(original.VaultId), Convert.ToHexString(duplicate.VaultId));
        Assert.NotEqual(
            Convert.ToHexString(original.FileEntry("payload.bin").BlobId!),
            Convert.ToHexString(duplicate.FileEntry("payload.bin").BlobId!));

        (long originalOffset, long length) = original.BlobRange("payload.bin");
        (long duplicateOffset, long duplicateLength) = duplicate.BlobRange("payload.bin");
        Assert.Equal(length, duplicateLength);
        Assert.False(
            original.Bytes.AsSpan((int)originalOffset, (int)length)
                .SequenceEqual(duplicate.Bytes.AsSpan((int)duplicateOffset, (int)duplicateLength)),
            "a copy must not reproduce the ciphertext of its original");

        // Same plaintext, though: the copy is a copy.
        await using IVaultSession reopened = await OpenAsync(copy, OtherPassword, 66);
        Assert.True(reopened.TryResolvePath("\\payload.bin", out EntryId file));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)),
            Convert.ToHexString(SHA256.HashData(await GoldenFixtureTests.ReadAllAsync(reopened, file))));
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    /// <summary>Adds the wrap fields of the current file to the sets that must stay collision free.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="salts">Every salt seen so far.</param>
    /// <param name="nonces">Every wrap nonce seen so far.</param>
    /// <param name="pairs">Every salt and nonce combination seen so far.</param>
    private static void Record(string path, HashSet<string> salts, HashSet<string> nonces, HashSet<string> pairs)
    {
        (byte[] salt, byte[] nonce, byte[] _) = WrapOf(path);
        salts.Add(Convert.ToHexString(salt));
        nonces.Add(Convert.ToHexString(nonce));
        pairs.Add(Convert.ToHexString(salt) + Convert.ToHexString(nonce));
    }

    /// <summary>Reads the salt, the wrap nonce and the wrapped key out of a vault header.</summary>
    /// <param name="path">Path of the vault.</param>
    private static (byte[] Salt, byte[] Nonce, byte[] Wrapped) WrapOf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return (
            bytes.AsSpan(32, 32).ToArray(),
            bytes.AsSpan(64, 12).ToArray(),
            bytes.AsSpan(76, 48).ToArray());
    }

    /// <summary>The blob id of every file entry of a vault, by entry name.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="password">Password of the vault.</param>
    private static Dictionary<string, string> BlobIds(string path, string password)
    {
        using VaultImage image = VaultImage.Load(path, password);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IndexEntry entry in image.Index.Entries.Where(entry => entry.Kind == EntryKind.File))
        {
            ids[entry.Name] = Convert.ToHexString(entry.BlobId!);
        }

        return ids;
    }

    /// <summary>Adds every blob id of a snapshot to the set of ids that were ever used.</summary>
    /// <param name="everSeen">The set.</param>
    /// <param name="snapshot">Blob ids by entry name.</param>
    private static void AddAll(HashSet<string> everSeen, Dictionary<string, string> snapshot)
    {
        foreach (string id in snapshot.Values)
        {
            everSeen.Add(id);
        }
    }

    /// <summary>Deterministic content of a given length.</summary>
    /// <param name="length">Number of bytes.</param>
    /// <param name="seed">Seed of the block.</param>
    private static byte[] Content(int length, ulong seed)
    {
        byte[] content = new byte[length];
        new DeterministicRandomSource(seed).Fill(content);
        return content;
    }

    /// <summary>Creates a vault and returns the open session.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="seed">Seed of the randomness seam.</param>
    private static async Task<IVaultSession> CreateAsync(string path, ulong seed)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await new VaultFactory(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch))
            .CreateAsync(path, password, null, GoldenVault.Kdf, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a vault of this class.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="password">Password of the vault.</param>
    /// <param name="seed">Seed of the randomness seam.</param>
    private static async Task<IVaultSession> OpenAsync(string path, string password, ulong seed)
    {
        using Passphrase passphrase = Passphrase.FromString(password);
        return await new VaultFactory(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, passphrase, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }
}
