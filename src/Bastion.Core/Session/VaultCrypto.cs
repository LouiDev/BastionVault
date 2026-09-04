using Bastion.Core.Crypto;

namespace Bastion.Core.Session;

/// <summary>
/// The key set a session works with: the vault key, the vault id derived from it and the index key.
/// Disposing zeroes every buffer, which is exactly what <see cref="IVaultSession.Lock"/> does.
/// </summary>
internal sealed class VaultCrypto : IDisposable
{
    private VaultCrypto(KeyMaterial vaultKey, byte[] vaultId, KeyMaterial indexKey)
    {
        VaultKey = vaultKey;
        VaultId = vaultId;
        IndexKey = indexKey;
    }

    /// <summary>The 32-byte vault key.</summary>
    public KeyMaterial VaultKey { get; }

    /// <summary>The 16-byte vault id derived from the vault key; never stored in the file.</summary>
    public byte[] VaultId { get; }

    /// <summary>The 32-byte index key.</summary>
    public KeyMaterial IndexKey { get; }

    /// <summary>Derives the vault id and index key from a vault key and takes ownership of it.</summary>
    /// <param name="vaultKey">The vault key; the instance disposes it.</param>
    public static VaultCrypto Adopt(KeyMaterial vaultKey)
    {
        ArgumentNullException.ThrowIfNull(vaultKey);

        byte[]? vaultId = null;
        KeyMaterial? indexKey = null;
        try
        {
            vaultId = VaultKeys.DeriveVaultId(vaultKey.Span);
            indexKey = VaultKeys.DeriveIndexKey(vaultKey.Span);
            return new VaultCrypto(vaultKey, vaultId, indexKey);
        }
        catch
        {
            indexKey?.Dispose();
            vaultKey.Dispose();
            throw;
        }
    }

    /// <summary>Creates a fresh random vault key and the keys derived from it.</summary>
    /// <param name="random">Randomness seam.</param>
    public static VaultCrypto Create(IRandomSource random) => Adopt(KeyMaterial.Random(32, random));

    /// <summary>True when both key sets share the same vault key, so blobs may be copied verbatim.</summary>
    /// <param name="other">The other key set.</param>
    public bool SharesKeySpaceWith(VaultCrypto other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) ||
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(VaultKey.Span, other.VaultKey.Span);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IndexKey.Dispose();
        VaultKey.Dispose();
        Array.Clear(VaultId);
    }
}
