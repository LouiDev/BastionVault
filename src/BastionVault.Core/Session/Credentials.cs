using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using BastionVault.Core.Crypto;

namespace BastionVault.Core.Session;

/// <summary>
/// A password, keyfile or KDF change that has been derived but not written yet. It stays pending until
/// the next save, which is the single commit point (FORMAT.md section 8.4).
/// </summary>
internal sealed class PendingCredentials : IDisposable
{
    /// <summary>Argon2id parameters to store in the new header.</summary>
    public required KdfParameters Kdf { get; init; }

    /// <summary>The fresh 32-byte salt the KEK was derived with.</summary>
    public required byte[] KdfSalt { get; init; }

    /// <summary>The derived key-encryption key.</summary>
    public required KeyMaterial Kek { get; init; }

    /// <summary>Whether the save re-keys the vault or only rewraps the existing vault key.</summary>
    public required CredentialChangeMode Mode { get; init; }

    /// <summary>The vault key the save will install, for <see cref="CredentialChangeMode.Rekey"/>.</summary>
    public KeyMaterial? NewVaultKey { get; init; }

    /// <inheritdoc />
    public void Dispose()
    {
        Kek.Dispose();
        NewVaultKey?.Dispose();
        CryptographicOperations.ZeroMemory(KdfSalt);
    }
}

/// <summary>Runs the password KDF and turns a password plus an optional keyfile into a KEK.</summary>
internal static partial class Credentials
{
    /// <summary>Length of the Argon2id tag and of every derived key.</summary>
    private const int KeyLength = 32;

    /// <summary>
    /// Runs Argon2id on the thread pool and derives the KEK (FORMAT.md section 2.3). The KDF phase itself
    /// is not interruptible; the token is honoured between passes and again right after it returns.
    /// </summary>
    /// <param name="kdf">Key-derivation seam.</param>
    /// <param name="password">The password.</param>
    /// <param name="keyFile">The keyfile, or <see langword="null"/>.</param>
    /// <param name="salt">The 32-byte KDF salt.</param>
    /// <param name="parameters">Argon2id cost parameters.</param>
    /// <param name="progress">Progress sink; the KDF phase reports that it cannot be cancelled.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<KeyMaterial> DeriveKekAsync(
        IKeyDerivation kdf,
        Passphrase password,
        KeyFile? keyFile,
        byte[] salt,
        KdfParameters parameters,
        ProgressThrottle? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kdf);
        ArgumentNullException.ThrowIfNull(password);

        ct.ThrowIfCancellationRequested();
        progress?.Start(null, isCancellable: false);

        // The span of a Passphrase cannot cross an await, so the bytes move into a pinned buffer first.
        using KeyMaterial secret = KeyMaterial.From(password.Bytes);
        byte[]? argon2 = null;
        try
        {
            argon2 = await Task.Run(
                () => kdf.DeriveArgon2id(secret.Span, salt, parameters, KeyLength, ct),
                ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            return keyFile is null
                ? VaultKeys.DeriveKek(argon2, ReadOnlySpan<byte>.Empty, salt)
                : VaultKeys.DeriveKek(argon2, keyFile.Digest, salt);
        }
        finally
        {
            if (argon2 is not null)
            {
                CryptographicOperations.ZeroMemory(argon2);
            }
        }
    }

    /// <summary>
    /// FORMAT.md section 3.1 step 9: refuse a KDF that would claim more than 75 % of the memory the
    /// machine physically has. Installed memory, not free memory: what is free moves with whatever else
    /// the machine is doing this second, so measuring it refused the default preset on a large machine
    /// during a busy moment. The pre-flight exists to reject a header no machine of this size could
    /// ever serve, and that question has a stable answer.
    /// </summary>
    /// <param name="parameters">Argon2id parameters from the header.</param>
    /// <exception cref="VaultResourceException"><see cref="VaultErrorCode.ResourceLimit"/>.</exception>
    public static void PreflightMemory(KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        long installed = InstalledPhysicalMemoryBytes();
        if (installed <= 0)
        {
            return;
        }

        long budget = (long)(installed * Format.VaultLimits.KdfMemoryFractionOfInstalled);
        long required = parameters.MemoryBytes;
        if (required <= budget)
        {
            return;
        }

        throw new VaultResourceException(
            VaultErrorCode.ResourceLimit,
            $"Opening this vault needs {Mebibytes(required)} MiB of memory for the key derivation; " +
            $"this machine has {Mebibytes(installed)} MiB installed.")
        {
            RequiredBytes = required,
            AvailableBytes = budget,
        };
    }

    /// <summary>
    /// Installed physical memory in bytes, which is what FORMAT.md section 3.1 step 9 measures:
    /// <c>GlobalMemoryStatusEx.ullTotalPhys</c>, falling back to
    /// <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> (the machine's or the container's total)
    /// where that call is unavailable.
    /// </summary>
    /// <returns>Installed physical memory, or 0 when nothing can be measured.</returns>
    internal static long InstalledPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical is > 0 and <= long.MaxValue)
            {
                return (long)status.TotalPhysical;
            }
        }

        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : 0;
    }

    /// <summary>Rounds a byte count up to whole mebibytes, for the message.</summary>
    /// <param name="bytes">Byte count to convert.</param>
    private static long Mebibytes(long bytes) => (bytes + (1024 * 1024) - 1) / (1024 * 1024);

    /// <summary>The subset of <c>MEMORYSTATUSEX</c> the pre-flight needs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        /// <summary>Size of the structure in bytes; set by the caller.</summary>
        public uint Length;

        /// <summary>Percentage of physical memory in use.</summary>
        public uint MemoryLoad;

        /// <summary>Total physical memory: the quantity section 3.1 step 9 asks for.</summary>
        public ulong TotalPhysical;

        /// <summary>Free physical memory; not consulted by the pre-flight.</summary>
        public ulong AvailablePhysical;

        /// <summary>Committed memory limit.</summary>
        public ulong TotalPageFile;

        /// <summary>Remaining commit charge.</summary>
        public ulong AvailablePageFile;

        /// <summary>Size of the process virtual address space.</summary>
        public ulong TotalVirtual;

        /// <summary>Unreserved address space.</summary>
        public ulong AvailableVirtual;

        /// <summary>Reserved; always zero.</summary>
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>Queries the machine's memory state.</summary>
    /// <param name="buffer">Structure to fill; its <c>Length</c> must be set.</param>
    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
