namespace BastionVault.Core;

/// <summary>
/// Named Argon2id cost levels (FORMAT.md §7). All presets use <c>p = 4</c>.
/// </summary>
public enum KdfPreset
{
    /// <summary>64 MiB, t = 3, p = 4.</summary>
    Fast,

    /// <summary>512 MiB, t = 3, p = 4. The default for new vaults.</summary>
    Standard,

    /// <summary>1 GiB, t = 4, p = 4.</summary>
    Strong,
}
