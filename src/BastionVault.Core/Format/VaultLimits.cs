namespace BastionVault.Core.Format;

/// <summary>
/// The normative limits table of FORMAT.md section 7, plus the structural sizes of the v1 format.
/// Readers enforce every one of these before exposing any plaintext.
/// </summary>
public static class VaultLimits
{
    /// <summary>Length of the plaintext header in bytes.</summary>
    public const int HeaderSize = 160;

    /// <summary>Length of an AES-GCM authentication tag in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Minimum Argon2id memory cost in KiB (8 MiB). Also at least <c>8 * parallelism</c>.</summary>
    public const uint MinKdfMemoryKiB = 8192;

    /// <summary>Maximum Argon2id memory cost in KiB (4 GiB). Must be a multiple of <c>4 * parallelism</c>.</summary>
    public const uint MaxKdfMemoryKiB = 4194304;

    /// <summary>Minimum Argon2id pass count.</summary>
    public const uint MinKdfIterations = 1;

    /// <summary>Maximum Argon2id pass count.</summary>
    public const uint MaxKdfIterations = 64;

    /// <summary>Minimum Argon2id lane count.</summary>
    public const uint MinKdfParallelism = 1;

    /// <summary>Maximum Argon2id lane count.</summary>
    public const uint MaxKdfParallelism = 16;

    /// <summary>Share of installed physical memory the KDF may claim before <see cref="VaultErrorCode.ResourceLimit"/> is raised.</summary>
    public const double KdfMemoryFractionOfInstalled = 0.75;

    /// <summary>Minimum UTF-8 password length in bytes.</summary>
    public const int MinPasswordBytes = 1;

    /// <summary>Maximum UTF-8 password length in bytes.</summary>
    public const int MaxPasswordBytes = 1024;

    /// <summary>Minimum keyfile length in bytes.</summary>
    public const int MinKeyFileBytes = 1;

    /// <summary>Maximum keyfile length in bytes (1 MiB).</summary>
    public const int MaxKeyFileBytes = 1024 * 1024;

    /// <summary>Minimum encrypted index length in bytes (64 KiB of padded plaintext plus the tag).</summary>
    public const long MinIndexLength = 65552;

    /// <summary>Maximum encrypted index length in bytes (64 MiB plus the tag).</summary>
    public const long MaxIndexLength = (64L * 1024 * 1024) + 16;

    /// <summary>Maximum padded index plaintext length in bytes (64 MiB).</summary>
    public const long MaxIndexPlaintext = 64L * 1024 * 1024;

    /// <summary>Maximum number of entries in one index.</summary>
    public const int MaxEntries = 1_000_000;

    /// <summary>Maximum tree depth, with the root counting as 0.</summary>
    public const int MaxDepth = 128;

    /// <summary>Minimum entry name length in UTF-16 code units.</summary>
    public const int MinNameCodeUnits = 1;

    /// <summary>Maximum entry name length in UTF-16 code units.</summary>
    public const int MaxNameCodeUnits = 255;

    /// <summary>Maximum entry name length in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 765;

    /// <summary>Maximum comment length in UTF-8 bytes.</summary>
    public const int MaxCommentBytes = 4096;

    /// <summary>Minimum chunk size in bytes (64 KiB).</summary>
    public const uint MinChunkSize = 65536;

    /// <summary>Maximum chunk size in bytes (64 MiB).</summary>
    public const uint MaxChunkSize = 67108864;

    /// <summary>Chunk size this writer uses (1 MiB).</summary>
    public const uint DefaultChunkSize = 1024 * 1024;

    /// <summary>Maximum plaintext length of one file in bytes (2^48 - 1).</summary>
    public const long MaxFileLength = (1L << 48) - 1;

    /// <summary>Minimum number of chunks in a blob; an empty file still has one empty chunk.</summary>
    public const uint MinChunkCount = 1;

    /// <summary>Maximum number of chunks in a blob (2^32 - 1), so the chunk counter never wraps.</summary>
    public const uint MaxChunkCount = uint.MaxValue;

    /// <summary>Aggregate staged ciphertext held in memory before the staging container is used (64 MiB).</summary>
    public const long InMemoryStagingLimit = 64L * 1024 * 1024;
}
