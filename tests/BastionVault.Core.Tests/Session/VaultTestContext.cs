using System.Security.Cryptography;

namespace BastionVault.Core.Tests.Session;

/// <summary>
/// A throwaway vault directory plus the deterministic seams every session test uses: a fixed clock, a
/// seeded random source and a deliberately tiny Argon2id cost so the suite stays fast.
/// </summary>
internal sealed class VaultTestContext : IDisposable
{
    /// <summary>The password every test uses unless it needs a second one.</summary>
    public const string Password = "correct horse battery staple";

    /// <summary>The second password, for credential-change tests.</summary>
    public const string OtherPassword = "a different pass phrase entirely";

    /// <summary>8 MiB, one pass, one lane: valid per FORMAT.md section 7 and fast enough for a unit test.</summary>
    public static readonly KdfParameters FastKdf = new(8192, 1, 1);

    private ulong _seed;

    /// <summary>Creates an empty temporary directory for one test.</summary>
    public VaultTestContext()
    {
        Root = Path.Combine(Path.GetTempPath(), "BastionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        VaultPath = Path.Combine(Root, "test.bastion");
        SourceDirectory = Path.Combine(Root, "source");
        Directory.CreateDirectory(SourceDirectory);
    }

    /// <summary>The temporary directory holding everything this test creates.</summary>
    public string Root { get; }

    /// <summary>Path of the vault under test.</summary>
    public string VaultPath { get; }

    /// <summary>A directory for files that get imported.</summary>
    public string SourceDirectory { get; }

    /// <summary>The clock every factory uses.</summary>
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Creates a factory with a fresh deterministic seed.</summary>
    public VaultFactory NewFactory() => new(new DeterministicRandomSource(++_seed), Clock);

    /// <summary>Creates the vault under test and returns the open session.</summary>
    /// <param name="password">Password to protect it with.</param>
    /// <param name="keyFile">Optional keyfile.</param>
    /// <param name="kdf">Optional cost parameters.</param>
    public async Task<IVaultSession> CreateAsync(string password = Password, KeyFile? keyFile = null, KdfParameters? kdf = null)
    {
        using Passphrase passphrase = Passphrase.FromString(password);
        return await NewFactory()
            .CreateAsync(VaultPath, passphrase, keyFile, kdf ?? FastKdf, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Opens the vault under test.</summary>
    /// <param name="password">Password to try.</param>
    /// <param name="keyFile">Optional keyfile.</param>
    /// <param name="options">Open options.</param>
    public Task<IVaultSession> OpenAsync(string password = Password, KeyFile? keyFile = null, OpenOptions? options = null) =>
        OpenOtherAsync(VaultPath, password, keyFile, options);

    /// <summary>Opens any vault in this test directory.</summary>
    /// <param name="path">Path of the vault.</param>
    /// <param name="password">Password to try.</param>
    /// <param name="keyFile">Optional keyfile.</param>
    /// <param name="options">Open options.</param>
    public async Task<IVaultSession> OpenOtherAsync(string path, string password = Password, KeyFile? keyFile = null, OpenOptions? options = null)
    {
        using Passphrase passphrase = Passphrase.FromString(password);
        return await NewFactory()
            .OpenAsync(path, passphrase, keyFile, options ?? OpenOptions.Default, null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the vault, closes it and reopens it with the given options.</summary>
    /// <param name="options">Options for the second open.</param>
    public async Task<IVaultSession> CreateThenOpenAsync(OpenOptions options)
    {
        await using (IVaultSession session = await CreateAsync().ConfigureAwait(false))
        {
        }

        return await OpenAsync(options: options).ConfigureAwait(false);
    }

    /// <summary>Writes a file into the source directory.</summary>
    /// <param name="name">File name.</param>
    /// <param name="content">Content to write.</param>
    /// <returns>The full path of the file.</returns>
    public string WriteSourceFile(string name, byte[] content)
    {
        string path = Path.Combine(SourceDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>A reproducible pseudo-random byte block.</summary>
    /// <param name="length">Number of bytes.</param>
    /// <param name="seed">Seed of the block.</param>
    public static byte[] Bytes(int length, int seed)
    {
        byte[] buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    /// <summary>Reads a whole entry through <see cref="IVaultSession.OpenReadAsync"/>.</summary>
    /// <param name="session">Session to read from.</param>
    /// <param name="id">File entry to read.</param>
    public static async Task<byte[]> ReadAllAsync(IVaultSession session, EntryId id)
    {
        await using Stream stream = await session.OpenReadAsync(id, CancellationToken.None).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Flips one bit of the vault file at an absolute offset.</summary>
    /// <param name="path">File to damage.</param>
    /// <param name="offset">Offset of the byte to flip.</param>
    public static void FlipByte(string path, long offset)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = offset;
        int value = stream.ReadByte();
        stream.Position = offset;
        stream.WriteByte((byte)(value ^ 0x40));
    }

    /// <summary>Finds the first entry with a given name anywhere in the vault.</summary>
    /// <param name="session">Session to search.</param>
    /// <param name="name">Name to look for.</param>
    public static EntryInfo Entry(IVaultSession session, string name)
    {
        IReadOnlyList<EntryInfo> hits = session.Search(name, null, 100, CancellationToken.None);
        return hits.Single(entry => entry.Name == name);
    }

    /// <summary>The SHA-256 of a byte block, as a hex string, for readable assertions.</summary>
    /// <param name="content">Content to hash.</param>
    public static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
