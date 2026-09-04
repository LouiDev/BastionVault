using System.Text;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// A healthy vault with known contents plus the scaffolding every adversarial test needs: a pristine
/// byte image to mutate, a target path to write the mutation to, an export directory that must stay
/// empty and a plaintext marker that must never appear anywhere on disk.
/// </summary>
internal sealed class TamperVault : IDisposable
{
    /// <summary>The password every tamper vault uses.</summary>
    public const string Password = "correct horse battery staple";

    /// <summary>A byte sequence that only exists inside the vault; finding it on disk means plaintext leaked.</summary>
    public const string Marker = "BASTION-PLAINTEXT-MARKER-0123456789";

    /// <summary>Name of the file carrying <see cref="Marker"/>.</summary>
    public const string MarkerFile = "marker.txt";

    /// <summary>Name of the first of two files whose blobs have identical lengths.</summary>
    public const string TwinA = "twin-a.bin";

    /// <summary>Name of the second of two files whose blobs have identical lengths.</summary>
    public const string TwinB = "twin-b.bin";

    /// <summary>Name of the multi-chunk file.</summary>
    public const string BigFile = "big.bin";

    /// <summary>Plaintext length of <see cref="BigFile"/>: three chunks, the last one short.</summary>
    public const int BigLength = (2 * 1024 * 1024) + 17;

    private readonly TempDirectory _work;

    /// <summary>Creates the scaffolding around an already written vault.</summary>
    /// <param name="work">The scratch directory owning every file.</param>
    /// <param name="originalPath">Path of the pristine vault.</param>
    /// <param name="original">Pristine bytes of the vault.</param>
    private TamperVault(TempDirectory work, string originalPath, byte[] original)
    {
        _work = work;
        OriginalPath = originalPath;
        Original = original;
        TargetPath = Path.Combine(work.Path, "target", "tampered.bastion");
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
        ExportDirectory = work.SubDirectory("export");
    }

    /// <summary>The scratch directory; everything the test writes lives under it.</summary>
    public string Root => _work.Path;

    /// <summary>Path of the pristine vault.</summary>
    public string OriginalPath { get; }

    /// <summary>The pristine bytes; never mutate this array.</summary>
    public byte[] Original { get; }

    /// <summary>Path a mutated image is written to, in its own directory so leftovers are easy to spot.</summary>
    public string TargetPath { get; }

    /// <summary>An export directory that must still be empty after every rejected open.</summary>
    public string ExportDirectory { get; }

    /// <summary>The 2 MiB + 17 byte content of <see cref="BigFile"/>.</summary>
    public static byte[] BigContent()
    {
        byte[] content = new byte[BigLength];
        new DeterministicRandomSource(31337).Fill(content);
        return content;
    }

    /// <summary>The 35-byte content of <see cref="MarkerFile"/>.</summary>
    public static byte[] MarkerContent() => Encoding.ASCII.GetBytes(Marker);

    /// <summary>Creates and saves a healthy vault.</summary>
    /// <param name="withBigFile">Whether to include the multi-chunk <see cref="BigFile"/>.</param>
    /// <param name="seed">Seed of the randomness seam, so two vaults can differ deliberately.</param>
    public static async Task<TamperVault> CreateAsync(bool withBigFile = true, ulong seed = 11)
    {
        var work = new TempDirectory("tamper");
        try
        {
            string sources = work.SubDirectory("source");
            await File.WriteAllBytesAsync(Path.Combine(sources, MarkerFile), MarkerContent()).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(sources, TwinA), "aaa"u8.ToArray()).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(sources, TwinB), "bbb"u8.ToArray()).ConfigureAwait(false);
            if (withBigFile)
            {
                await File.WriteAllBytesAsync(Path.Combine(sources, BigFile), BigContent()).ConfigureAwait(false);
            }

            string vaultPath = Path.Combine(work.Path, "original.bastion");
            var factory = new VaultFactory(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch));
            var options = new ImportOptions(PreserveTimestamps: false);

            using (Passphrase password = Passphrase.FromString(Password))
            {
                await using IVaultSession session = await factory
                    .CreateAsync(vaultPath, password, null, GoldenVault.Kdf, null, CancellationToken.None)
                    .ConfigureAwait(false);

                List<string> paths = [Path.Combine(sources, MarkerFile), Path.Combine(sources, TwinA), Path.Combine(sources, TwinB)];
                if (withBigFile)
                {
                    paths.Add(Path.Combine(sources, BigFile));
                }

                foreach (string path in paths)
                {
                    await session.ImportAsync(EntryId.Root, [path], options, null, CancellationToken.None).ConfigureAwait(false);
                }

                await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None).ConfigureAwait(false);
            }

            return new TamperVault(work, vaultPath, await File.ReadAllBytesAsync(vaultPath).ConfigureAwait(false));
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    /// <summary>A fresh, mutable copy of the pristine image.</summary>
    public byte[] Copy() => (byte[])Original.Clone();

    /// <summary>An opened image of the pristine vault; the caller disposes it.</summary>
    public VaultImage Image() => VaultImage.Load(OriginalPath, Password);

    /// <summary>Writes a mutated image to <see cref="TargetPath"/>.</summary>
    /// <param name="bytes">The bytes to write.</param>
    public void Write(byte[] bytes)
    {
        if (File.Exists(TargetPath))
        {
            File.Delete(TargetPath);
        }

        File.WriteAllBytes(TargetPath, bytes);
    }

    /// <summary>Opens <see cref="TargetPath"/> with the vault password.</summary>
    /// <param name="seed">Seed of the factory's randomness seam.</param>
    /// <param name="kdf">Key-derivation seam; the real Argon2id when omitted.</param>
    public async Task<IVaultSession> OpenTargetAsync(ulong seed = 5, BastionVault.Core.Crypto.IKeyDerivation? kdf = null)
    {
        using Passphrase password = Passphrase.FromString(Password);
        return await new VaultFactory(new DeterministicRandomSource(seed), new FixedClock(GoldenVault.Epoch), null, kdf)
            .OpenAsync(TargetPath, password, null, OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a mutated image and asserts that opening it is refused with a <see cref="VaultException"/>,
    /// which is returned for further assertions.
    /// </summary>
    /// <param name="bytes">The mutated image.</param>
    /// <param name="because">A description of the mutation, used in assertion messages.</param>
    /// <param name="kdf">Key-derivation seam; the real Argon2id when omitted.</param>
    public async Task<VaultException> ExpectOpenFailsAsync(byte[] bytes, string because, BastionVault.Core.Crypto.IKeyDerivation? kdf = null)
    {
        Write(bytes);

        try
        {
            await using IVaultSession session = await OpenTargetAsync(kdf: kdf).ConfigureAwait(false);
        }
        catch (VaultException expected)
        {
            AssertNoPlaintextEscaped();
            return expected;
        }

        Assert.Fail($"Opening the vault succeeded although {because}.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Asserts that no file below the scratch directory contains the plaintext marker, that the export
    /// directory is still empty and that no partial or temporary output was left behind.
    /// </summary>
    public void AssertNoPlaintextEscaped()
    {
        Assert.Empty(Directory.GetFileSystemEntries(ExportDirectory));

        byte[] marker = MarkerContent();
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".partial", StringComparison.Ordinal) || name.Contains(".tmp-", StringComparison.Ordinal))
            {
                Assert.Fail($"A partial or temporary output was left behind: {file}");
            }

            // The source directory legitimately holds the marker; nothing else may.
            if (file.StartsWith(Path.Combine(Root, "source"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] content = File.ReadAllBytes(file);
            Assert.False(
                Contains(content, marker),
                $"The plaintext marker escaped into {file}.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _work.Dispose();

    /// <summary>True when <paramref name="needle"/> occurs in <paramref name="haystack"/>.</summary>
    /// <param name="haystack">Bytes to search.</param>
    /// <param name="needle">Bytes to find.</param>
    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle.AsSpan()) >= 0;
}
