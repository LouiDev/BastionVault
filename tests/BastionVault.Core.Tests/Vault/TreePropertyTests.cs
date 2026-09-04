using System.Security.Cryptography;
using System.Text;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// A property test over whole vaults: build a random tree with unicode names and small contents, save
/// it, close it, open it again and export it, and require that every step preserves the tree exactly.
/// Anything the writer, the index serializer, the reader and the exporter disagree about shows up here
/// as a missing entry, a renamed one or a content mismatch.
/// </summary>
public sealed class TreePropertyTests
{
    /// <summary>Largest number of entries a generated tree may have.</summary>
    private const int MaxEntries = 200;

    /// <summary>Name fragments that exercise the parts of FORMAT.md section 6.1 a writer must survive.</summary>
    private static readonly string[] Fragments =
    [
        "café", "naïve", "日本語", "Ωmega", "тест", "emoji😀", "with space", "dot.in.name",
        "Ärger", "ελληνικά", "한국어", "mixed CASE", "tilde~", "plus+minus-", "(paren)", "hash#",
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task A_random_tree_survives_save_reopen_and_export(int seed)
    {
        using var work = new TempDirectory($"property-{seed}");
        var random = new Random(seed);
        List<ModelEntry> model = Generate(random);

        string sources = work.SubDirectory("source");
        string path = Path.Combine(work.Path, "property.bastion");

        using (Passphrase password = Passphrase.FromString(TamperVault.Password))
        {
            var factory = new VaultFactory(new DeterministicRandomSource((ulong)seed), new FixedClock(GoldenVault.Epoch));
            await using IVaultSession session = await factory.CreateAsync(
                path, password, null, GoldenVault.Kdf, null, CancellationToken.None);

            await BuildAsync(session, model, sources);
            AssertMatches(session, model);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            AssertMatches(session, model);
        }

        await using IVaultSession reopened = await OpenAsync(path);

        AssertMatches(reopened, model);
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);

        string destination = work.SubDirectory("export");
        ExportResult export = await reopened.ExportAsync(
            [.. reopened.GetChildren(EntryId.Root).Select(entry => entry.Id)],
            destination,
            new ExportOptions(),
            null,
            CancellationToken.None);

        Assert.Empty(export.Issues);
        Assert.Equal(model.Count(entry => entry.IsFile), export.FilesWritten);
        AssertExported(destination, model);
    }

    [Fact]
    public async Task A_tree_at_the_documented_depth_limit_survives_a_round_trip()
    {
        using var work = new TempDirectory("property-depth");
        string path = Path.Combine(work.Path, "deep.bastion");
        string sources = work.SubDirectory("source");
        string file = Path.Combine(sources, "leaf.bin");
        await File.WriteAllBytesAsync(file, "leaf"u8.ToArray());

        using (Passphrase password = Passphrase.FromString(TamperVault.Password))
        {
            var factory = new VaultFactory(new DeterministicRandomSource(99), new FixedClock(GoldenVault.Epoch));
            await using IVaultSession session = await factory.CreateAsync(
                path, password, null, GoldenVault.Kdf, null, CancellationToken.None);

            EntryId parent = EntryId.Root;
            EntryId lastFolderThatCanHoldAFile = EntryId.Root;
            for (int level = 1; level <= VaultLimits.MaxDepth; level++)
            {
                if (level == VaultLimits.MaxDepth)
                {
                    lastFolderThatCanHoldAFile = parent;
                }

                parent = await session.CreateFolderAsync(parent, $"level{level}", CancellationToken.None);
            }

            // One level deeper than the format allows.
            VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                () => session.CreateFolderAsync(parent, "too deep", CancellationToken.None));
            VaultAssert.Failure(error, VaultErrorCode.InvalidMove, "level 129");

            var options = new ImportOptions(PreserveTimestamps: false);

            // A file inside the deepest folder would sit at depth 129, so the import reports it instead
            // of building a tree the index serializer would refuse.
            ImportResult refused = await session.ImportAsync(parent, [file], options, null, CancellationToken.None);
            Assert.Empty(refused.Imported);
            Assert.Equal(ImportIssueKind.TooDeep, Assert.Single(refused.Issues).Kind);

            ImportResult accepted = await session.ImportAsync(
                lastFolderThatCanHoldAFile, [file], options, null, CancellationToken.None);
            Assert.Single(accepted.Imported);
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await using IVaultSession reopened = await OpenAsync(path);

        var segments = new StringBuilder();
        for (int level = 1; level < VaultLimits.MaxDepth; level++)
        {
            segments.Append('\\').Append("level").Append(level);
        }

        Assert.True(reopened.TryResolvePath(segments.Append("\\leaf.bin").ToString(), out EntryId leaf));
        Assert.Equal(4, reopened.Find(leaf)!.Length);
        Assert.Equal(VaultLimits.MaxDepth, reopened.GetAncestors(leaf).Count);
        Assert.Equal(VaultLimits.MaxDepth, reopened.Statistics.FolderCount);
    }

    /// <summary>Opens a vault of this class with its password.</summary>
    /// <param name="path">Path of the vault.</param>
    private static async Task<IVaultSession> OpenAsync(string path)
    {
        using Passphrase password = Passphrase.FromString(TamperVault.Password);
        return await new VaultFactory(new DeterministicRandomSource(1234), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Generates a random tree of folders and files.</summary>
    /// <param name="random">Random source.</param>
    private static List<ModelEntry> Generate(Random random)
    {
        var model = new List<ModelEntry>();
        var folders = new List<string> { string.Empty };
        int count = random.Next(20, MaxEntries + 1);

        for (int i = 0; i < count; i++)
        {
            string parent = folders[random.Next(folders.Count)];
            int depth = parent.Count(c => c == '\\');
            bool folder = depth < 6 && random.Next(100) < 35;
            string name = $"{Fragments[random.Next(Fragments.Length)]} {i}" + (folder ? string.Empty : ".bin");
            string path = parent + "\\" + name;

            if (folder)
            {
                folders.Add(path);
                model.Add(new ModelEntry(path, null));
                continue;
            }

            byte[] content = new byte[random.Next(0, 2049)];
            random.NextBytes(content);
            model.Add(new ModelEntry(path, content));
        }

        return model;
    }

    /// <summary>Creates the generated tree inside a session.</summary>
    /// <param name="session">Session to fill.</param>
    /// <param name="model">The generated tree.</param>
    /// <param name="sources">Directory the file contents are written to before importing them.</param>
    private static async Task BuildAsync(IVaultSession session, List<ModelEntry> model, string sources)
    {
        var options = new ImportOptions(PreserveTimestamps: false);
        var folders = new Dictionary<string, EntryId>(StringComparer.OrdinalIgnoreCase) { [string.Empty] = EntryId.Root };

        for (int i = 0; i < model.Count; i++)
        {
            ModelEntry entry = model[i];
            EntryId parent = folders[entry.Parent];

            if (!entry.IsFile)
            {
                folders[entry.Path] = await session.CreateFolderAsync(parent, entry.Name, CancellationToken.None);
                continue;
            }

            // The disk name stays ASCII: the unicode name is applied inside the vault, so the test does
            // not depend on what the file system of the build machine accepts.
            string source = Path.Combine(sources, $"import-{i}.bin");
            await File.WriteAllBytesAsync(source, entry.Content!);

            ImportResult result = await session.ImportAsync(parent, [source], options, null, CancellationToken.None);
            EntryId imported = Assert.Single(result.Imported);
            await session.RenameAsync(imported, entry.Name, CancellationToken.None);
        }
    }

    /// <summary>Asserts that a session holds exactly the generated tree.</summary>
    /// <param name="session">Session to inspect.</param>
    /// <param name="model">The generated tree.</param>
    private static void AssertMatches(IVaultSession session, List<ModelEntry> model)
    {
        foreach (ModelEntry entry in model)
        {
            Assert.True(session.TryResolvePath(entry.Path, out EntryId id), $"{entry.Path} is missing");
            EntryInfo info = session.Find(id)!;
            Assert.Equal(entry.Name, info.Name);
            Assert.Equal(entry.IsFile ? EntryKind.File : EntryKind.Folder, info.Kind);
            Assert.Equal(entry.Path, session.FormatPath(id));

            if (entry.IsFile)
            {
                Assert.Equal(entry.Content!.Length, info.Length);
            }
        }

        var statistics = session.Statistics;
        Assert.Equal(model.Count(entry => entry.IsFile), statistics.FileCount);
        Assert.Equal(model.Count(entry => !entry.IsFile), statistics.FolderCount);
        Assert.Equal(model.Where(entry => entry.IsFile).Sum(entry => (long)entry.Content!.Length), statistics.TotalPlaintextBytes);
    }

    /// <summary>Asserts that an export directory holds exactly the generated tree.</summary>
    /// <param name="destination">Export root.</param>
    /// <param name="model">The generated tree.</param>
    private static void AssertExported(string destination, List<ModelEntry> model)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories))
        {
            found.Add("\\" + Path.GetRelativePath(destination, entry));
        }

        foreach (ModelEntry entry in model)
        {
            Assert.Contains(entry.Path, found);
            if (!entry.IsFile)
            {
                continue;
            }

            byte[] exported = File.ReadAllBytes(destination + entry.Path);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(entry.Content!)),
                Convert.ToHexString(SHA256.HashData(exported)));
        }

        Assert.Equal(model.Count, found.Count);
    }

    /// <summary>One generated entry.</summary>
    /// <param name="Path">Full in-vault path, backslash separated.</param>
    /// <param name="Content">File content, or <see langword="null"/> for a folder.</param>
    private sealed record ModelEntry(string Path, byte[]? Content)
    {
        /// <summary>True for a file entry.</summary>
        public bool IsFile => Content is not null;

        /// <summary>Name of the entry.</summary>
        public string Name => Path[(Path.LastIndexOf('\\') + 1)..];

        /// <summary>Path of the parent folder; empty for the root.</summary>
        public string Parent => Path[..Path.LastIndexOf('\\')];
    }
}
