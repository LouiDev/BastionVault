namespace Bastion.Core;

/// <summary>Settings for <see cref="IVaultSession.SaveAsync"/> and <see cref="IVaultSession.SaveCopyAsync"/>.</summary>
/// <param name="SizeObfuscation">
/// Pad the data section with CSPRNG bytes up to the obfuscation ladder of FORMAT.md §5 so the
/// file length no longer reveals the exact content size.
/// </param>
public sealed record SaveOptions(bool SizeObfuscation = false)
{
    /// <summary>The default save settings (no size obfuscation).</summary>
    public static readonly SaveOptions Default = new();
}

/// <summary>Settings for <see cref="IVaultFactory.OpenAsync"/>.</summary>
/// <param name="ReadOnly">Open without the ability to save; every mutation throws <see cref="VaultErrorCode.ReadOnlySession"/>.</param>
/// <param name="StagingDirectoryOverride">User-chosen directory for the staging container, or <see langword="null"/> for the default placement (FORMAT.md §8.5).</param>
/// <param name="InMemoryStagingLimit">Aggregate staged ciphertext held in memory before the container file is used; 64 MiB by default.</param>
public sealed record OpenOptions(
    bool ReadOnly = false,
    string? StagingDirectoryOverride = null,
    long InMemoryStagingLimit = 64L * 1024 * 1024)
{
    /// <summary>The default open settings (read-write, default staging placement, 64 MiB in memory).</summary>
    public static readonly OpenOptions Default = new();
}
