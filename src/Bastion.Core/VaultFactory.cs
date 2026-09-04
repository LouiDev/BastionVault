using System.Security.Cryptography;
using Bastion.Core.Crypto;
using Bastion.Core.Format;
using Bastion.Core.Session;
using Microsoft.Win32.SafeHandles;

namespace Bastion.Core;

/// <summary>
/// Default <see cref="IVaultFactory"/>. All non-determinism (randomness, time, file naming) and the
/// key-derivation implementation are injected, so tests can make a whole vault byte-for-byte reproducible.
/// </summary>
public sealed class VaultFactory : IVaultFactory
{
    private readonly IRandomSource _random;
    private readonly IClock _clock;
    private readonly IVaultPaths _paths;
    private readonly IKeyDerivation _kdf;

    /// <summary>Creates a factory, defaulting every seam to its production implementation.</summary>
    /// <param name="random">Randomness seam; the OS CSPRNG by default.</param>
    /// <param name="clock">Time seam; the system clock by default.</param>
    /// <param name="paths">Temp, backup and staging file naming; <see cref="DefaultVaultPaths"/> by default.</param>
    /// <param name="kdf">Key derivation; the built-in Argon2id by default.</param>
    public VaultFactory(IRandomSource? random = null, IClock? clock = null, IVaultPaths? paths = null,
                        IKeyDerivation? kdf = null)
    {
        _random = random ?? SystemRandomSource.Instance;
        _clock = clock ?? SystemClock.Instance;
        _paths = paths ?? new DefaultVaultPaths(_random);
        _kdf = kdf ?? Argon2.Instance;
    }

    /// <inheritdoc />
    public async Task<VaultHeaderInfo> ReadHeaderAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        string full = Path.GetFullPath(path);
        SafeFileHandle handle = OpenForReading(full);
        try
        {
            long length = RandomAccess.GetLength(handle);
            byte[] headerBytes = new byte[VaultHeader.Size];
            if (length >= VaultHeader.Size)
            {
                await FileIo.ReadExactlyAsync(handle, 0, headerBytes, ct).ConfigureAwait(false);
            }

            VaultHeader header = VaultHeader.Parse(headerBytes, length);
            return new VaultHeaderInfo(header.FormatVersion, header.Kdf, length, header.IndexLength, header.Kdf.MemoryBytes);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, full);
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<IVaultSession> CreateAsync(
        string path, Passphrase password, KeyFile? keyFile, KdfParameters kdf,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(kdf);
        kdf.Validate();
        ct.ThrowIfCancellationRequested();

        string full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new VaultIoException(VaultErrorCode.IoError, $"A file already exists at {full}.") { OffendingPath = full };
        }

        Credentials.PreflightMemory(kdf);

        byte[] salt = new byte[32];
        _random.Fill(salt);

        var throttle = new ProgressThrottle(progress, VaultOperation.Create);
        using KeyMaterial kek = await Credentials
            .DeriveKekAsync(_kdf, password, keyFile, salt, kdf, throttle, ct)
            .ConfigureAwait(false);

        VaultCrypto crypto = VaultCrypto.Create(_random);
        try
        {
            var request = new SaveRequest
            {
                DestinationPath = full,
                ReplaceExisting = false,
                Entries = [],
                NextEntryId = 1,
                SourceCrypto = crypto,
                DestinationCrypto = crypto,
                Mode = SaveMode.Compact,
                Wrap = new WrapPlan { Kdf = kdf, KdfSalt = salt, Kek = kek },
                SaveCounter = 1,
                SavedUtc = _clock.UtcNow,
                SizeObfuscation = false,
                Operation = VaultOperation.Create,
            };

            SaveResult result = await new SaveWriter(_random, _paths).RunAsync(request, progress, ct).ConfigureAwait(false);

            SafeFileHandle handle = VaultSession.OpenVaultHandle(full);
            return new VaultSession(
                full,
                new VaultFileHandle(handle),
                result.Header,
                result.Index,
                crypto,
                OpenOptions.Default,
                readOnly: false,
                openedFromIndexCopy: false,
                result.Stat,
                _random,
                _clock,
                _paths,
                _kdf);
        }
        catch (Exception ex)
        {
            crypto.Dispose();
            throw IoGuard.Translate(ex, full);
        }
    }

    /// <inheritdoc />
    public async Task<IVaultSession> OpenAsync(
        string path, Passphrase password, KeyFile? keyFile, OpenOptions options,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (directory is not null)
        {
            // Startup sweep (FORMAT.md section 8.5): reclaim what a crashed session left behind.
            TrySweep(directory);
        }

        SafeFileHandle handle = OpenForReading(full);
        VaultCrypto? crypto = null;
        try
        {
            long length = RandomAccess.GetLength(handle);
            byte[] headerBytes = new byte[VaultHeader.Size];
            if (length >= VaultHeader.Size)
            {
                await FileIo.ReadExactlyAsync(handle, 0, headerBytes, ct).ConfigureAwait(false);
            }

            // Steps 1 to 8 of FORMAT.md section 3.1.
            VaultHeader header = VaultHeader.Parse(headerBytes, length);

            // Step 9: the machine must be able to afford the key derivation.
            Credentials.PreflightMemory(header.Kdf);

            var throttle = new ProgressThrottle(progress, VaultOperation.Open);
            using (KeyMaterial kek = await Credentials
                .DeriveKekAsync(_kdf, password, keyFile, header.KdfSalt, header.Kdf, throttle, ct)
                .ConfigureAwait(false))
            {
                crypto = VaultCrypto.Adopt(HeaderCipher.UnwrapVaultKey(
                    kek.Span, header.WrapNonce, header.WrappedVaultKey, header.BuildWrapAad()));
            }

            ct.ThrowIfCancellationRequested();

            (VaultIndex index, bool fromCopy) = await ReadIndexAsync(handle, header, crypto, length, ct).ConfigureAwait(false);

            // Section 4.6 rule 11: the length equation must hold exactly once the index is known.
            long expected = VaultHeader.Size + (2 * header.IndexLength) + index.DataSectionLength;
            if (length != expected)
            {
                throw new VaultFormatException(
                    VaultErrorCode.Truncated,
                    $"The vault file is {length} bytes long; its header and index describe exactly {expected} bytes.");
            }

            FileStat stat = FileIo.TryStat(full)
                ?? throw new VaultIoException(VaultErrorCode.IoError, $"The vault file {full} disappeared while it was being opened.")
                { OffendingPath = full };

            bool readOnly = options.ReadOnly || IsReadOnlyTarget(full, directory);
            throttle.Complete(0, 1, Path.GetFileName(full));

            return new VaultSession(
                full,
                new VaultFileHandle(handle),
                header,
                index,
                crypto,
                options,
                readOnly,
                fromCopy,
                stat,
                _random,
                _clock,
                _paths,
                _kdf);
        }
        catch (Exception ex)
        {
            crypto?.Dispose();
            handle.Dispose();
            throw IoGuard.Translate(ex, full);
        }
    }

    /// <inheritdoc />
    public Task<long> SweepOrphansAsync(IEnumerable<string> directories, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(directories);

        try
        {
            return Task.FromResult(OrphanSweeper.Sweep(directories, ct));
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, null);
        }
    }

    /// <summary>
    /// Decrypts the index, falling back to the authenticated copy when the primary one does not
    /// authenticate (FORMAT.md section 4.1).
    /// </summary>
    /// <param name="handle">Handle on the vault file.</param>
    /// <param name="header">The parsed header.</param>
    /// <param name="crypto">The unwrapped keys.</param>
    /// <param name="fileLength">Length of the file.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<(VaultIndex Index, bool FromCopy)> ReadIndexAsync(
        SafeFileHandle handle, VaultHeader header, VaultCrypto crypto, long fileLength, CancellationToken ct)
    {
        byte[] indexAad = header.BuildIndexAad();
        byte[] ciphertext = new byte[header.IndexLength];
        await FileIo.ReadExactlyAsync(handle, VaultHeader.Size, ciphertext, ct).ConfigureAwait(false);

        byte[]? plaintext = null;
        bool fromCopy = false;
        try
        {
            plaintext = HeaderCipher.DecryptIndex(crypto.IndexKey.Span, header.IndexNonce, ciphertext, indexAad);
        }
        catch (VaultFormatException primaryFailure) when (primaryFailure.Code == VaultErrorCode.IndexCorrupt)
        {
            // The copy sits at the very end of the file; its offset follows from the length equation.
            await FileIo.ReadExactlyAsync(handle, fileLength - header.IndexLength, ciphertext, ct).ConfigureAwait(false);
            plaintext = HeaderCipher.DecryptIndex(crypto.IndexKey.Span, header.IndexCopyNonce, ciphertext, indexAad);
            fromCopy = true;
        }

        try
        {
            return (IndexSerializer.Deserialize(plaintext), fromCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Opens the vault file the way a session holds it, mapping a sharing violation to a vault error.</summary>
    /// <param name="path">Path of the vault file.</param>
    private static SafeFileHandle OpenForReading(string path)
    {
        try
        {
            return VaultSession.OpenVaultHandle(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new VaultIoException(VaultErrorCode.IoError, $"There is no file at {path}.", ex) { OffendingPath = path };
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new VaultIoException(VaultErrorCode.IoError, $"There is no directory for {path}.", ex) { OffendingPath = path };
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, path);
        }
    }

    /// <summary>True when the vault cannot be written: read-only attribute or a directory that refuses writes.</summary>
    /// <param name="path">Path of the vault file.</param>
    /// <param name="directory">Directory holding the vault.</param>
    private static bool IsReadOnlyTarget(string path, string? directory)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return directory is not null && !StagingStore.IsWritable(directory);
    }

    /// <summary>Sweeps one directory, ignoring every failure.</summary>
    /// <param name="directory">Directory to sweep.</param>
    private static void TrySweep(string directory)
    {
        try
        {
            OrphanSweeper.Sweep([directory], CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping never blocks opening a vault.
        }
    }
}
