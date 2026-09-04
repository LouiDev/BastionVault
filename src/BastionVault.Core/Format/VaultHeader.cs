using System.Buffers.Binary;

namespace BastionVault.Core.Format;

/// <summary>
/// The 160-byte plaintext header (FORMAT.md section 3). It discloses nothing about the contents
/// beyond the KDF cost and the padded index size, and carries no CRC and no vault identifier.
/// </summary>
public sealed class VaultHeader
{
    /// <summary>Header length in bytes.</summary>
    public const int Size = 160;

    /// <summary>The only format version this build reads and writes.</summary>
    private const ushort SupportedFormatVersion = 1;

    /// <summary>The only KDF id defined in v1: Argon2id.</summary>
    private const byte KdfIdArgon2id = 1;

    /// <summary>The only cipher id implemented in v1: AES-256-GCM. Id 2 (ChaCha20-Poly1305) is reserved.</summary>
    private const byte CipherIdAesGcm = 1;

    /// <summary>Mask of the critical flag bits (0..15); an unknown critical bit is rejected.</summary>
    private const uint CriticalFlagMask = 0x0000_FFFFu;

    private const int OffsetMagic = 0;
    private const int OffsetFormatVersion = 8;
    private const int OffsetHeaderLength = 10;
    private const int OffsetFlags = 12;
    private const int OffsetKdfId = 16;
    private const int OffsetCipherId = 17;
    private const int OffsetReserved0 = 18;
    private const int OffsetKdfMemoryKiB = 20;
    private const int OffsetKdfIterations = 24;
    private const int OffsetKdfParallelism = 28;
    private const int OffsetKdfSalt = 32;
    private const int OffsetWrapNonce = 64;
    private const int OffsetWrappedVaultKey = 76;
    private const int OffsetIndexNonce = 124;
    private const int OffsetIndexCopyNonce = 136;
    private const int OffsetIndexLength = 148;
    private const int OffsetReserved1 = 156;

    /// <summary>Length of the KDF salt in bytes.</summary>
    private const int KdfSaltSize = 32;

    /// <summary>Length of a GCM nonce in bytes.</summary>
    private const int NonceSize = 12;

    /// <summary>Length of the wrapped vault key in bytes (32 bytes ciphertext plus a 16-byte tag).</summary>
    private const int WrappedVaultKeySize = 48;

    /// <summary>First byte zeroed in the key-wrap AAD (FORMAT.md section 2.6).</summary>
    private const int WrapAadZeroFrom = 76;

    /// <summary>End (exclusive) of the range zeroed in the key-wrap AAD.</summary>
    private const int WrapAadZeroTo = 156;

    /// <summary>First byte zeroed in the index AAD (FORMAT.md section 2.6).</summary>
    private const int IndexAadZeroFrom = 124;

    /// <summary>End (exclusive) of the range zeroed in the index AAD.</summary>
    private const int IndexAadZeroTo = 148;

    /// <summary>The magic bytes <c>89 42 53 54 4E 0D 0A 1A</c>; anything else is not a vault.</summary>
    public static ReadOnlySpan<byte> Magic => new byte[] { 0x89, 0x42, 0x53, 0x54, 0x4E, 0x0D, 0x0A, 0x1A };

    /// <summary>Format version; 1 for v1.</summary>
    public ushort FormatVersion { get; init; } = 1;

    /// <summary>Feature flags. Bits 0..15 are critical, bits 16..31 advisory; v1 defines none and writes 0.</summary>
    public uint Flags { get; init; }

    /// <summary>Argon2id cost parameters.</summary>
    public KdfParameters Kdf { get; init; } = KdfParameters.Default;

    /// <summary>The 32-byte KDF salt; fresh whenever the vault key is wrapped.</summary>
    public byte[] KdfSalt { get; init; } = [];

    /// <summary>The 12-byte nonce of the key wrap; fresh whenever the vault key is wrapped.</summary>
    public byte[] WrapNonce { get; init; } = [];

    /// <summary>The 48-byte wrapped vault key (32 bytes ciphertext and a 16-byte tag).</summary>
    public byte[] WrappedVaultKey { get; init; } = [];

    /// <summary>The 12-byte nonce of the primary index; fresh on every save.</summary>
    public byte[] IndexNonce { get; init; } = [];

    /// <summary>The 12-byte nonce of the index copy; fresh on every save and different from <see cref="IndexNonce"/>.</summary>
    public byte[] IndexCopyNonce { get; init; } = [];

    /// <summary>Encrypted index length in bytes: padded plaintext plus the 16-byte tag.</summary>
    public long IndexLength { get; init; }

    /// <summary>
    /// Reads and validates a header per FORMAT.md section 3.1 steps 1 to 8. Performs no resource
    /// pre-flight and no key derivation.
    /// </summary>
    /// <param name="bytes">The first 160 bytes of the file.</param>
    /// <param name="fileLength">Total length of the file, used for the cheap half of the length equation.</param>
    /// <exception cref="VaultFormatException">
    /// <see cref="VaultErrorCode.NotAVault"/>, <see cref="VaultErrorCode.UnsupportedVersion"/>,
    /// <see cref="VaultErrorCode.HeaderCorrupt"/>, <see cref="VaultErrorCode.UnsupportedParameters"/>
    /// or <see cref="VaultErrorCode.Truncated"/>.
    /// </exception>
    public static VaultHeader Parse(ReadOnlySpan<byte> bytes, long fileLength)
    {
        // Step 1 - the file must be long enough to hold a header at all.
        if (fileLength < Size)
        {
            throw Fail(
                VaultErrorCode.Truncated,
                $"The file is {fileLength} bytes long; a vault begins with a {Size}-byte header.");
        }

        if (bytes.Length < Size)
        {
            throw Fail(
                VaultErrorCode.Truncated,
                $"Only {bytes.Length} header bytes were supplied; {Size} are required.");
        }

        ReadOnlySpan<byte> header = bytes[..Size];

        // Step 2 - magic.
        if (!header[OffsetMagic..(OffsetMagic + Magic.Length)].SequenceEqual(Magic))
        {
            throw Fail(VaultErrorCode.NotAVault, "The file does not start with the Bastion Vault signature.");
        }

        // Step 3 - format version.
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[OffsetFormatVersion..]);
        if (formatVersion == 0)
        {
            throw Fail(VaultErrorCode.HeaderCorrupt, "The header declares format version 0, which does not exist.");
        }

        if (formatVersion > SupportedFormatVersion)
        {
            throw Fail(
                VaultErrorCode.UnsupportedVersion,
                $"The vault uses format version {formatVersion}; this build understands version {SupportedFormatVersion}.");
        }

        // Step 4 - structural constants.
        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(header[OffsetHeaderLength..]);
        if (headerLength != Size)
        {
            throw Fail(
                VaultErrorCode.HeaderCorrupt,
                $"The header declares a length of {headerLength} bytes; version 1 headers are exactly {Size} bytes.");
        }

        ushort reserved0 = BinaryPrimitives.ReadUInt16LittleEndian(header[OffsetReserved0..]);
        if (reserved0 != 0)
        {
            throw Fail(VaultErrorCode.HeaderCorrupt, "A reserved header field at offset 18 is not zero.");
        }

        uint reserved1 = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetReserved1..]);
        if (reserved1 != 0)
        {
            throw Fail(VaultErrorCode.HeaderCorrupt, "A reserved header field at offset 156 is not zero.");
        }

        // Step 5 - critical flags and algorithm ids.
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetFlags..]);
        uint criticalFlags = flags & CriticalFlagMask;
        if (criticalFlags != 0)
        {
            throw Fail(
                VaultErrorCode.UnsupportedParameters,
                $"The header sets critical feature flags 0x{criticalFlags:X4} that this build does not implement.");
        }

        byte kdfId = header[OffsetKdfId];
        if (kdfId != KdfIdArgon2id)
        {
            throw Fail(
                VaultErrorCode.UnsupportedParameters,
                $"The vault uses key-derivation function {kdfId}; only Argon2id ({KdfIdArgon2id}) is supported.");
        }

        byte cipherId = header[OffsetCipherId];
        if (cipherId != CipherIdAesGcm)
        {
            string detail = cipherId == 2
                ? "ChaCha20-Poly1305 (2) is reserved and not implemented in version 1"
                : $"cipher id {cipherId} is unknown";
            throw Fail(
                VaultErrorCode.UnsupportedParameters,
                $"The vault uses an unsupported cipher: {detail}. Only AES-256-GCM ({CipherIdAesGcm}) is supported.");
        }

        // Step 6 - KDF cost parameters against the limits table.
        var kdf = new KdfParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetKdfMemoryKiB..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetKdfIterations..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[OffsetKdfParallelism..]));
        kdf.Validate();

        // Step 7 - index length and the cheap half of the length equation.
        ulong indexLength = BinaryPrimitives.ReadUInt64LittleEndian(header[OffsetIndexLength..]);
        if (indexLength < (ulong)VaultLimits.MinIndexLength || indexLength > (ulong)VaultLimits.MaxIndexLength)
        {
            throw Fail(
                VaultErrorCode.HeaderCorrupt,
                $"The header declares an index length of {indexLength} bytes; it must be between " +
                $"{VaultLimits.MinIndexLength} and {VaultLimits.MaxIndexLength}.");
        }

        long requiredLength = Size + (2 * (long)indexLength);
        if (fileLength < requiredLength)
        {
            throw Fail(
                VaultErrorCode.Truncated,
                $"The file is {fileLength} bytes long but the header requires at least {requiredLength} bytes " +
                "for the header and both index copies.");
        }

        // Step 8 - the two index nonces must differ (they encrypt the same plaintext).
        ReadOnlySpan<byte> indexNonce = header.Slice(OffsetIndexNonce, NonceSize);
        ReadOnlySpan<byte> indexCopyNonce = header.Slice(OffsetIndexCopyNonce, NonceSize);
        if (indexNonce.SequenceEqual(indexCopyNonce))
        {
            throw Fail(VaultErrorCode.HeaderCorrupt, "The index nonce and the index copy nonce are identical.");
        }

        return new VaultHeader
        {
            FormatVersion = formatVersion,
            Flags = flags,
            Kdf = kdf,
            KdfSalt = header.Slice(OffsetKdfSalt, KdfSaltSize).ToArray(),
            WrapNonce = header.Slice(OffsetWrapNonce, NonceSize).ToArray(),
            WrappedVaultKey = header.Slice(OffsetWrappedVaultKey, WrappedVaultKeySize).ToArray(),
            IndexNonce = indexNonce.ToArray(),
            IndexCopyNonce = indexCopyNonce.ToArray(),
            IndexLength = (long)indexLength,
        };
    }

    /// <summary>Writes exactly 160 bytes in the layout of FORMAT.md section 3.</summary>
    /// <param name="destination">Destination buffer, at least 160 bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Size"/>.</exception>
    /// <exception cref="InvalidOperationException">A field of this header cannot be represented on disk.</exception>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException(
                $"A vault header needs {Size} bytes; the destination holds {destination.Length}.",
                nameof(destination));
        }

        KdfParameters kdf = Kdf ?? throw new InvalidOperationException("Kdf must be set before a header is written.");
        if (IndexLength < 0)
        {
            throw new InvalidOperationException($"IndexLength must not be negative (was {IndexLength}).");
        }

        Span<byte> header = destination[..Size];
        header.Clear();

        Magic.CopyTo(header[OffsetMagic..]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[OffsetFormatVersion..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[OffsetHeaderLength..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header[OffsetFlags..], Flags);
        header[OffsetKdfId] = KdfIdArgon2id;
        header[OffsetCipherId] = CipherIdAesGcm;

        // reserved0 at 18 and reserved1 at 156 stay zero (the buffer was cleared).
        BinaryPrimitives.WriteUInt32LittleEndian(header[OffsetKdfMemoryKiB..], kdf.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(header[OffsetKdfIterations..], kdf.Iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(header[OffsetKdfParallelism..], kdf.Parallelism);

        CopyExact(KdfSalt, header.Slice(OffsetKdfSalt, KdfSaltSize), nameof(KdfSalt));
        CopyExact(WrapNonce, header.Slice(OffsetWrapNonce, NonceSize), nameof(WrapNonce));
        CopyExact(WrappedVaultKey, header.Slice(OffsetWrappedVaultKey, WrappedVaultKeySize), nameof(WrappedVaultKey));
        CopyExact(IndexNonce, header.Slice(OffsetIndexNonce, NonceSize), nameof(IndexNonce));
        CopyExact(IndexCopyNonce, header.Slice(OffsetIndexCopyNonce, NonceSize), nameof(IndexCopyNonce));

        BinaryPrimitives.WriteUInt64LittleEndian(header[OffsetIndexLength..], (ulong)IndexLength);
    }

    /// <summary>
    /// Builds the key-wrap AAD: <c>"bastion/v1/wrap"</c> followed by the 160 header bytes with
    /// bytes [76, 156) zeroed.
    /// </summary>
    public byte[] BuildWrapAad() => BuildAad("bastion/v1/wrap"u8, WrapAadZeroFrom, WrapAadZeroTo);

    /// <summary>
    /// Builds the index AAD: <c>"bastion/v1/index"</c> followed by the 160 header bytes with
    /// bytes [124, 148) zeroed. The same AAD covers the index and its copy.
    /// </summary>
    public byte[] BuildIndexAad() => BuildAad("bastion/v1/index"u8, IndexAadZeroFrom, IndexAadZeroTo);

    /// <summary>Offset of the data section: the header length plus the index length.</summary>
    public long DataSectionOffset => Size + IndexLength;

    /// <summary>Serializes the header behind an ASCII label and zeroes the given byte range.</summary>
    /// <param name="label">Domain-separation label, written without a terminator.</param>
    /// <param name="zeroFrom">First header byte to zero.</param>
    /// <param name="zeroTo">End (exclusive) of the header bytes to zero.</param>
    private byte[] BuildAad(ReadOnlySpan<byte> label, int zeroFrom, int zeroTo)
    {
        byte[] aad = new byte[label.Length + Size];
        label.CopyTo(aad);

        Span<byte> header = aad.AsSpan(label.Length, Size);
        Write(header);
        header[zeroFrom..zeroTo].Clear();
        return aad;
    }

    /// <summary>Copies a fixed-size field, rejecting a source of the wrong length.</summary>
    /// <param name="source">Field value.</param>
    /// <param name="destination">Slice of the header the field occupies.</param>
    /// <param name="fieldName">Property name, for the error message.</param>
    private static void CopyExact(byte[] source, Span<byte> destination, string fieldName)
    {
        if (source is null || source.Length != destination.Length)
        {
            throw new InvalidOperationException(
                $"{fieldName} must be exactly {destination.Length} bytes (was {source?.Length.ToString() ?? "null"}).");
        }

        source.CopyTo(destination);
    }

    /// <summary>Creates the exception for a rejected header.</summary>
    /// <param name="code">Error code from FORMAT.md section 3.1.</param>
    /// <param name="message">Human-readable message; never contains key material.</param>
    private static VaultFormatException Fail(VaultErrorCode code, string message) => new(code, message);
}
