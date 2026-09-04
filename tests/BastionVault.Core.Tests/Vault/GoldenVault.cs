namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The recipe behind the two golden fixtures of FORMAT.md section 10. Everything that could vary
/// between runs is pinned: the randomness seam, the clock, the KDF cost, the password, the order of
/// operations and the file contents. Two runs on two machines must produce identical bytes.
/// </summary>
internal static class GoldenVault
{
    /// <summary>The password both fixtures are protected with.</summary>
    public const string Password = "correct horse battery staple";

    /// <summary>File name of the empty fixture.</summary>
    public const string EmptyFixtureName = "golden-v1-empty.bastion";

    /// <summary>File name of the small fixture.</summary>
    public const string SmallFixtureName = "golden-v1-small.bastion";

    /// <summary>Plaintext length of <c>big.bin</c>: two megabytes and 17 bytes, so the last chunk is short.</summary>
    public const int BigLength = (2 * 1024 * 1024) + 17;

    /// <summary>Content of <c>a.txt</c>.</summary>
    public const string SmallText = "abc";

    /// <summary>The comment carried by <c>a.txt</c>; deliberately not ASCII.</summary>
    public const string Comment = "Golden fixture — café / 日本語 / tab\there";

    /// <summary>Seed of the randomness seam of both fixtures.</summary>
    public const ulong Seed = 0;

    /// <summary>Seed of the pseudo-random block that fills <c>big.bin</c>.</summary>
    private const ulong ContentSeed = 4242;

    /// <summary>The smallest parameter set FORMAT.md section 7 allows: 8 MiB, one pass, one lane.</summary>
    public static KdfParameters Kdf => new(8192, 1, 1);

    /// <summary>The instant both fixtures are stamped with.</summary>
    public static DateTimeOffset Epoch => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The deterministic content of <c>big.bin</c>.</summary>
    public static byte[] BigContent()
    {
        byte[] content = new byte[BigLength];
        new DeterministicRandomSource(ContentSeed).Fill(content);
        return content;
    }

    /// <summary>Builds the empty fixture and returns its bytes.</summary>
    /// <param name="workDirectory">A scratch directory the vault is built in.</param>
    public static async Task<byte[]> BuildEmptyAsync(string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        Directory.CreateDirectory(workDirectory);

        string vaultPath = Path.Combine(workDirectory, EmptyFixtureName);
        var factory = new VaultFactory(new DeterministicRandomSource(Seed), new FixedClock(Epoch));

        using Passphrase password = Passphrase.FromString(Password);
        await using (IVaultSession session = await factory
            .CreateAsync(vaultPath, password, null, Kdf, null, CancellationToken.None)
            .ConfigureAwait(false))
        {
        }

        return await File.ReadAllBytesAsync(vaultPath).ConfigureAwait(false);
    }

    /// <summary>Builds the small fixture and returns its bytes.</summary>
    /// <param name="workDirectory">A scratch directory the vault and its import sources are built in.</param>
    public static async Task<byte[]> BuildSmallAsync(string workDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        string sourceDirectory = Path.Combine(workDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);

        string aTxt = Path.Combine(sourceDirectory, "a.txt");
        string emptyBin = Path.Combine(sourceDirectory, "empty.bin");
        string bigBin = Path.Combine(sourceDirectory, "big.bin");
        await File.WriteAllBytesAsync(aTxt, "abc"u8.ToArray()).ConfigureAwait(false);
        await File.WriteAllBytesAsync(emptyBin, []).ConfigureAwait(false);
        await File.WriteAllBytesAsync(bigBin, BigContent()).ConfigureAwait(false);

        string vaultPath = Path.Combine(workDirectory, SmallFixtureName);
        var factory = new VaultFactory(new DeterministicRandomSource(Seed), new FixedClock(Epoch));

        // Timestamps must not come from the file system, or the fixture would change on every checkout.
        var options = new ImportOptions(PreserveTimestamps: false);

        using Passphrase password = Passphrase.FromString(Password);
        await using (IVaultSession session = await factory
            .CreateAsync(vaultPath, password, null, Kdf, null, CancellationToken.None)
            .ConfigureAwait(false))
        {
            EntryId documents = await session
                .CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None).ConfigureAwait(false);
            EntryId year = await session
                .CreateFolderAsync(documents, "2026", CancellationToken.None).ConfigureAwait(false);

            ImportResult text = await session
                .ImportAsync(year, [aTxt], options, null, CancellationToken.None).ConfigureAwait(false);
            await session.ImportAsync(EntryId.Root, [emptyBin], options, null, CancellationToken.None).ConfigureAwait(false);
            await session.ImportAsync(EntryId.Root, [bigBin], options, null, CancellationToken.None).ConfigureAwait(false);

            await session.SetCommentAsync(text.Imported[0], Comment, CancellationToken.None).ConfigureAwait(false);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None).ConfigureAwait(false);
        }

        return await File.ReadAllBytesAsync(vaultPath).ConfigureAwait(false);
    }
}

/// <summary>Locates the checked-in fixture directory of FORMAT.md section 10.</summary>
internal static class FixtureFiles
{
    /// <summary>
    /// The directory holding the golden fixtures: the source tree's <c>tests/fixtures</c> when the tests
    /// run from inside a checkout, otherwise the <c>fixtures</c> folder the project links into the output.
    /// </summary>
    public static string Directory { get; } = Resolve();

    /// <summary>The absolute path of one fixture.</summary>
    /// <param name="name">File name of the fixture.</param>
    public static string Path(string name) => System.IO.Path.Combine(Directory, name);

    /// <summary>True when the environment asks for the fixtures to be rewritten instead of compared.</summary>
    public static bool RegenerationRequested =>
        Environment.GetEnvironmentVariable("BASTION_REGEN_GOLDEN") is "1" or "true" or "TRUE";

    /// <summary>Walks up from the test binaries looking for the checkout, falling back to the output copy.</summary>
    private static string Resolve()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string fixtures = System.IO.Path.Combine(directory.FullName, "tests", "fixtures");
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "BastionVault.slnx")) &&
                System.IO.Directory.Exists(fixtures))
            {
                return fixtures;
            }
        }

        return System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
    }
}
