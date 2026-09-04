using Bastion.Core.Format;

namespace Bastion.Core;

/// <summary>
/// Argon2id cost parameters as stored in the vault header (FORMAT.md §3, §7).
/// </summary>
/// <param name="MemoryKiB">Memory cost in KiB. 8192 .. 4 194 304, at least <c>8 * Parallelism</c> and a multiple of <c>4 * Parallelism</c>.</param>
/// <param name="Iterations">Number of passes (Argon2 <c>t</c>). 1 .. 64.</param>
/// <param name="Parallelism">Number of lanes (Argon2 <c>p</c>). 1 .. 16.</param>
public sealed record KdfParameters(uint MemoryKiB, uint Iterations, uint Parallelism)
{
    /// <summary>Returns the parameters of a named preset: Fast 64 MiB/3/4, Standard 512 MiB/3/4, Strong 1 GiB/4/4.</summary>
    /// <param name="preset">The preset to materialise.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is not a defined value.</exception>
    public static KdfParameters FromPreset(KdfPreset preset) => preset switch
    {
        KdfPreset.Fast => new KdfParameters(65536, 3, 4),
        KdfPreset.Standard => new KdfParameters(524288, 3, 4),
        KdfPreset.Strong => new KdfParameters(1048576, 4, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown KDF preset."),
    };

    /// <summary>The parameters used for new vaults (<see cref="KdfPreset.Standard"/>).</summary>
    public static KdfParameters Default => FromPreset(KdfPreset.Standard);

    /// <summary>The preset these parameters equal exactly, or <see langword="null"/> when they are custom.</summary>
    public KdfPreset? MatchingPreset
    {
        get
        {
            if (this == FromPreset(KdfPreset.Fast))
            {
                return KdfPreset.Fast;
            }

            if (this == FromPreset(KdfPreset.Standard))
            {
                return KdfPreset.Standard;
            }

            if (this == FromPreset(KdfPreset.Strong))
            {
                return KdfPreset.Strong;
            }

            return null;
        }
    }

    /// <summary>Memory cost in bytes.</summary>
    public long MemoryBytes => (long)MemoryKiB * 1024;

    /// <summary>Throws when the parameters violate the limits table of FORMAT.md §7.</summary>
    /// <exception cref="VaultFormatException">
    /// <see cref="VaultErrorCode.UnsupportedParameters"/> — a value is out of range or the memory cost
    /// is not compatible with the requested parallelism.
    /// </exception>
    public void Validate()
    {
        string? problem = Describe();
        if (problem is not null)
        {
            throw new VaultFormatException(VaultErrorCode.UnsupportedParameters, problem);
        }
    }

    /// <summary>True when <see cref="Validate"/> would not throw.</summary>
    public bool IsValid => Describe() is null;

    /// <summary>Returns a human-readable description of the first violated limit, or <see langword="null"/> when valid.</summary>
    private string? Describe()
    {
        if (Parallelism is < VaultLimits.MinKdfParallelism or > VaultLimits.MaxKdfParallelism)
        {
            return $"kdfParallelism must be between {VaultLimits.MinKdfParallelism} and {VaultLimits.MaxKdfParallelism} (was {Parallelism}).";
        }

        if (Iterations is < VaultLimits.MinKdfIterations or > VaultLimits.MaxKdfIterations)
        {
            return $"kdfIterations must be between {VaultLimits.MinKdfIterations} and {VaultLimits.MaxKdfIterations} (was {Iterations}).";
        }

        if (MemoryKiB is < VaultLimits.MinKdfMemoryKiB or > VaultLimits.MaxKdfMemoryKiB)
        {
            return $"kdfMemoryKiB must be between {VaultLimits.MinKdfMemoryKiB} and {VaultLimits.MaxKdfMemoryKiB} (was {MemoryKiB}).";
        }

        if (MemoryKiB < 8 * Parallelism)
        {
            return $"kdfMemoryKiB must be at least 8 * kdfParallelism = {8 * Parallelism} (was {MemoryKiB}).";
        }

        if (MemoryKiB % (4 * Parallelism) != 0)
        {
            return $"kdfMemoryKiB must be a multiple of 4 * kdfParallelism = {4 * Parallelism} (was {MemoryKiB}).";
        }

        return null;
    }
}
