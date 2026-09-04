using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BastionVault.Core.Crypto;

/// <summary>The three Argon2 variants of RFC 9106. The vault format only ever uses <see cref="Id"/>.</summary>
public enum Argon2Type
{
    /// <summary>Argon2d: data-dependent addressing.</summary>
    D = 0,

    /// <summary>Argon2i: data-independent addressing.</summary>
    I = 1,

    /// <summary>Argon2id: the hybrid used by this format.</summary>
    Id = 2,
}

/// <summary>The built-in Argon2 implementation (RFC 9106, version 0x13).</summary>
/// <remarks>
/// The working memory is one pinned <see cref="ulong"/> array of <c>m' * 128</c> words and is zeroed in a
/// <c>finally</c> block after every derivation, including a cancelled or failed one. Lanes run one task per
/// lane and join at every slice boundary, which is exactly the synchronisation RFC 9106 section 3.4 requires.
/// </remarks>
public sealed class Argon2 : IKeyDerivation
{
    /// <summary>Bytes in one Argon2 block.</summary>
    private const int BlockSizeBytes = 1024;

    /// <summary>64-bit words in one Argon2 block.</summary>
    private const int WordsPerBlock = BlockSizeBytes / 8;

    /// <summary>Slices per pass; lanes synchronise at every slice boundary.</summary>
    private const uint SyncPoints = 4;

    /// <summary>Pseudo-random values one address block of Argon2i carries.</summary>
    private const uint AddressesPerBlock = WordsPerBlock;

    /// <summary>The RFC 9106 version number written into H0.</summary>
    private const uint Version = 0x13;

    /// <summary>Shortest tag RFC 9106 allows.</summary>
    private const int MinTagLength = 4;

    /// <summary>Longest tag this implementation produces.</summary>
    private const int MaxTagLength = 1024 * 1024;

    /// <summary>Shortest salt RFC 9106 allows.</summary>
    private const int MinSaltLength = 8;

    /// <summary>Largest lane count RFC 9106 allows.</summary>
    private const uint MaxParallelism = 0xFFFFFF;

    /// <summary>Largest memory cost this implementation accepts, so that the word index stays an <see cref="int"/>.</summary>
    private const uint MaxMemoryKiB = 16_777_215;

    private Argon2()
    {
    }

    /// <summary>The shared instance; the class is stateless and thread-safe.</summary>
    public static readonly Argon2 Instance = new();

    /// <inheritdoc />
    public byte[] DeriveArgon2id(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int tagLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();
        return Hash(Argon2Type.Id, password, salt, default, default, parameters.MemoryKiB, parameters.Iterations, parameters.Parallelism, tagLength, ct);
    }

    /// <summary>
    /// Full RFC 9106 entry point used by the test vectors of FORMAT.md section 10; the secret and the
    /// associated data are optional and empty for vault use.
    /// </summary>
    /// <param name="type">Argon2 variant.</param>
    /// <param name="password">Password bytes.</param>
    /// <param name="salt">Salt bytes.</param>
    /// <param name="secret">Optional secret ("pepper").</param>
    /// <param name="associatedData">Optional associated data.</param>
    /// <param name="memoryKiB">Memory cost in KiB.</param>
    /// <param name="iterations">Number of passes.</param>
    /// <param name="parallelism">Number of lanes.</param>
    /// <param name="tagLength">Output length in bytes.</param>
    /// <param name="ct">Cancellation token, checked between passes.</param>
    /// <returns>A pinned array holding the tag; the caller should zero it when done.</returns>
    /// <exception cref="ArgumentException">The salt or the secret is too short or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A cost parameter or the tag length is outside the supported range.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was signalled between two passes.</exception>
    public static byte[] Hash(Argon2Type type, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
                              ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
                              uint memoryKiB, uint iterations, uint parallelism, int tagLength, CancellationToken ct)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("Argon2 blocks are little-endian; this build only supports little-endian platforms.");
        }

        if (type is not (Argon2Type.D or Argon2Type.I or Argon2Type.Id))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown Argon2 type.");
        }

        if (tagLength is < MinTagLength or > MaxTagLength)
        {
            throw new ArgumentOutOfRangeException(nameof(tagLength), tagLength, $"The tag length must be {MinTagLength} .. {MaxTagLength} bytes.");
        }

        if (salt.Length < MinSaltLength)
        {
            throw new ArgumentException($"The salt must be at least {MinSaltLength} bytes (was {salt.Length}).", nameof(salt));
        }

        if (parallelism is < 1 or > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(parallelism), parallelism, $"The lane count must be 1 .. {MaxParallelism}.");
        }

        if (iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "The pass count must be at least 1.");
        }

        if (memoryKiB > MaxMemoryKiB)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryKiB), memoryKiB, $"The memory cost must be at most {MaxMemoryKiB} KiB.");
        }

        if (memoryKiB < 8 * parallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryKiB), memoryKiB, "The memory cost must be at least 8 * parallelism KiB.");
        }

        return Derive(type, password, salt, secret, associatedData, memoryKiB, iterations, parallelism, tagLength, ct);
    }

    private static byte[] Derive(Argon2Type type, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
                                 ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
                                 uint memoryKiB, uint iterations, uint parallelism, int tagLength, CancellationToken ct)
    {
        uint lanes = parallelism;
        uint blocks = memoryKiB - (memoryKiB % (4 * lanes));   // m' = 4 * p * floor(m / (4 * p))
        uint laneLength = blocks / lanes;
        uint segmentLength = laneLength / SyncPoints;

        ulong[] memory = GC.AllocateUninitializedArray<ulong>((int)((long)blocks * WordsPerBlock), pinned: true);
        Span<byte> initial = stackalloc byte[72];   // H0 (64 bytes) || u32 blockIndex || u32 lane

        try
        {
            ComputeH0(type, password, salt, secret, associatedData, memoryKiB, iterations, lanes, tagLength, initial[..64]);

            for (uint lane = 0; lane < lanes; lane++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(initial.Slice(68, 4), lane);
                for (uint index = 0; index < 2; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(initial.Slice(64, 4), index);
                    VariableHash(initial, BlockBytes(memory, (lane * laneLength) + index));
                }
            }

            for (uint pass = 0; pass < iterations; pass++)
            {
                ct.ThrowIfCancellationRequested();

                for (uint slice = 0; slice < SyncPoints; slice++)
                {
                    FillSlice(memory, type, iterations, blocks, lanes, laneLength, segmentLength, pass, slice);
                }
            }

            return Finalize(memory, lanes, laneLength, tagLength);
        }
        finally
        {
            // Span.Clear() is a memset over the live pinned array; it cannot be elided.
            memory.AsSpan().Clear();
            CryptographicOperations.ZeroMemory(initial);
        }
    }

    private static void FillSlice(ulong[] memory, Argon2Type type, uint passes, uint blocks, uint lanes,
                                  uint laneLength, uint segmentLength, uint pass, uint slice)
    {
        if (lanes == 1)
        {
            FillSegment(memory, type, passes, blocks, lanes, laneLength, segmentLength, pass, 0, slice);
            return;
        }

        Task[] tasks = new Task[lanes - 1];
        for (uint lane = 1; lane < lanes; lane++)
        {
            uint current = lane;
            tasks[lane - 1] = Task.Run(() =>
                FillSegment(memory, type, passes, blocks, lanes, laneLength, segmentLength, pass, current, slice));
        }

        // Lane 0 runs inline, but the background lanes must be joined even when it throws: Derive's
        // finally zeroes the pinned memory, and an orphaned lane would write password-derived block
        // state back into the array after that, then leave it live for the GC.
        Exception? failure = null;
        try
        {
            FillSegment(memory, type, passes, blocks, lanes, laneLength, segmentLength, pass, 0, slice);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch when (failure is not null)
        {
            // The inline lane failed first; that is the failure worth reporting.
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private static void FillSegment(ulong[] memory, Argon2Type type, uint passes, uint blocks, uint lanes,
                                    uint laneLength, uint segmentLength, uint pass, uint lane, uint slice)
    {
        // Argon2id uses data-independent addressing for the first half of the first pass and
        // data-dependent addressing afterwards (RFC 9106 section 3.4.1.3).
        bool dataIndependent = type == Argon2Type.I || (type == Argon2Type.Id && pass == 0 && slice < SyncPoints / 2);

        Span<ulong> blockR = stackalloc ulong[WordsPerBlock];
        Span<ulong> blockTmp = stackalloc ulong[WordsPerBlock];
        Span<ulong> zeroBlock = stackalloc ulong[WordsPerBlock];
        Span<ulong> inputBlock = stackalloc ulong[WordsPerBlock];
        Span<ulong> addressBlock = stackalloc ulong[WordsPerBlock];
        zeroBlock.Clear();
        inputBlock.Clear();
        addressBlock.Clear();

        if (dataIndependent)
        {
            inputBlock[0] = pass;
            inputBlock[1] = lane;
            inputBlock[2] = slice;
            inputBlock[3] = blocks;
            inputBlock[4] = passes;
            inputBlock[5] = (uint)type;
        }

        uint startingIndex = 0;
        if (pass == 0 && slice == 0)
        {
            startingIndex = 2;   // the first two blocks of every lane come from H0
            if (dataIndependent)
            {
                NextAddresses(inputBlock, addressBlock, zeroBlock, blockR, blockTmp);
            }
        }

        ref ulong memoryBase = ref MemoryMarshal.GetArrayDataReference(memory);

        uint current = (lane * laneLength) + (slice * segmentLength) + startingIndex;
        uint previous = current % laneLength == 0 ? current + laneLength - 1 : current - 1;

        for (uint i = startingIndex; i < segmentLength; i++, current++, previous++)
        {
            if (current % laneLength == 1)
            {
                previous = current - 1;
            }

            ulong pseudoRandom;
            if (dataIndependent)
            {
                if (i % AddressesPerBlock == 0)
                {
                    NextAddresses(inputBlock, addressBlock, zeroBlock, blockR, blockTmp);
                }

                pseudoRandom = addressBlock[(int)(i % AddressesPerBlock)];
            }
            else
            {
                pseudoRandom = Unsafe.Add(ref memoryBase, (nint)previous * WordsPerBlock);
            }

            uint referenceLane = pass == 0 && slice == 0 ? lane : (uint)((pseudoRandom >> 32) % lanes);
            uint referenceIndex = IndexAlpha(pass, slice, i, laneLength, segmentLength, (uint)pseudoRandom, referenceLane == lane);
            uint referenceBlock = (referenceLane * laneLength) + referenceIndex;

            FillBlock(
                ref Unsafe.Add(ref memoryBase, (nint)previous * WordsPerBlock),
                ref Unsafe.Add(ref memoryBase, (nint)referenceBlock * WordsPerBlock),
                ref Unsafe.Add(ref memoryBase, (nint)current * WordsPerBlock),
                withXor: pass != 0,
                ref MemoryMarshal.GetReference(blockR),
                ref MemoryMarshal.GetReference(blockTmp));
        }

        blockR.Clear();
        blockTmp.Clear();
        inputBlock.Clear();
        addressBlock.Clear();
    }

    /// <summary>Regenerates the address block of the data-independent generator (RFC 9106 section 3.4.1.2).</summary>
    private static void NextAddresses(Span<ulong> inputBlock, Span<ulong> addressBlock, Span<ulong> zeroBlock,
                                      Span<ulong> blockR, Span<ulong> blockTmp)
    {
        inputBlock[6]++;
        FillBlock(
            ref MemoryMarshal.GetReference(zeroBlock),
            ref MemoryMarshal.GetReference(inputBlock),
            ref MemoryMarshal.GetReference(addressBlock),
            withXor: false,
            ref MemoryMarshal.GetReference(blockR),
            ref MemoryMarshal.GetReference(blockTmp));
        FillBlock(
            ref MemoryMarshal.GetReference(zeroBlock),
            ref MemoryMarshal.GetReference(addressBlock),
            ref MemoryMarshal.GetReference(addressBlock),
            withXor: false,
            ref MemoryMarshal.GetReference(blockR),
            ref MemoryMarshal.GetReference(blockTmp));
    }

    /// <summary>Maps a pseudo-random 32-bit value to a block index inside the reference set (RFC 9106 section 3.4.1.2).</summary>
    private static uint IndexAlpha(uint pass, uint slice, uint index, uint laneLength, uint segmentLength, uint pseudoRandom, bool sameLane)
    {
        ulong referenceAreaSize;
        if (pass == 0)
        {
            if (slice == 0)
            {
                referenceAreaSize = index - 1UL;
            }
            else if (sameLane)
            {
                referenceAreaSize = ((ulong)slice * segmentLength) + index - 1UL;
            }
            else
            {
                referenceAreaSize = ((ulong)slice * segmentLength) - (index == 0 ? 1UL : 0UL);
            }
        }
        else if (sameLane)
        {
            referenceAreaSize = (ulong)laneLength - segmentLength + index - 1UL;
        }
        else
        {
            referenceAreaSize = (ulong)laneLength - segmentLength - (index == 0 ? 1UL : 0UL);
        }

        ulong relative = pseudoRandom;
        relative = relative * relative >> 32;
        relative = referenceAreaSize - 1 - (referenceAreaSize * relative >> 32);

        ulong start = pass == 0 || slice == SyncPoints - 1 ? 0UL : (ulong)(slice + 1) * segmentLength;
        return (uint)((start + relative) % laneLength);
    }

    /// <summary>
    /// The Argon2 compression function G: <c>R = X xor Y</c>, the BlaMka permutation applied to the eight
    /// rows and then to the eight columns of R, xored back onto R (RFC 9106 section 3.5).
    /// </summary>
    private static void FillBlock(ref ulong previous, ref ulong reference, ref ulong next, bool withXor, ref ulong blockR, ref ulong blockTmp)
    {
        if (withXor)
        {
            for (int i = 0; i < WordsPerBlock; i++)
            {
                ulong r = Unsafe.Add(ref reference, i) ^ Unsafe.Add(ref previous, i);
                Unsafe.Add(ref blockR, i) = r;
                Unsafe.Add(ref blockTmp, i) = r ^ Unsafe.Add(ref next, i);
            }
        }
        else
        {
            for (int i = 0; i < WordsPerBlock; i++)
            {
                ulong r = Unsafe.Add(ref reference, i) ^ Unsafe.Add(ref previous, i);
                Unsafe.Add(ref blockR, i) = r;
                Unsafe.Add(ref blockTmp, i) = r;
            }
        }

        // Rows: words 16i .. 16i+15 (stride 2 between the eight 128-bit registers).
        for (int i = 0; i < 8; i++)
        {
            Permute(ref blockR, 16 * i, 2);
        }

        // Columns: words 2i, 2i+1, 2i+16, 2i+17, ... (stride 16).
        for (int i = 0; i < 8; i++)
        {
            Permute(ref blockR, 2 * i, 16);
        }

        for (int i = 0; i < WordsPerBlock; i++)
        {
            Unsafe.Add(ref next, i) = Unsafe.Add(ref blockTmp, i) ^ Unsafe.Add(ref blockR, i);
        }
    }

    /// <summary>
    /// The BLAKE2b round without message words on sixteen 64-bit words. Word <c>k</c> lives at
    /// <c>offset + (k / 2) * stride + (k % 2)</c>, which covers both the row layout (stride 2) and the
    /// column layout (stride 16) of the Argon2 block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Permute(ref ulong b, int offset, int stride)
    {
        ulong v0 = Unsafe.Add(ref b, offset);
        ulong v1 = Unsafe.Add(ref b, offset + 1);
        ulong v2 = Unsafe.Add(ref b, offset + stride);
        ulong v3 = Unsafe.Add(ref b, offset + stride + 1);
        ulong v4 = Unsafe.Add(ref b, offset + (2 * stride));
        ulong v5 = Unsafe.Add(ref b, offset + (2 * stride) + 1);
        ulong v6 = Unsafe.Add(ref b, offset + (3 * stride));
        ulong v7 = Unsafe.Add(ref b, offset + (3 * stride) + 1);
        ulong v8 = Unsafe.Add(ref b, offset + (4 * stride));
        ulong v9 = Unsafe.Add(ref b, offset + (4 * stride) + 1);
        ulong v10 = Unsafe.Add(ref b, offset + (5 * stride));
        ulong v11 = Unsafe.Add(ref b, offset + (5 * stride) + 1);
        ulong v12 = Unsafe.Add(ref b, offset + (6 * stride));
        ulong v13 = Unsafe.Add(ref b, offset + (6 * stride) + 1);
        ulong v14 = Unsafe.Add(ref b, offset + (7 * stride));
        ulong v15 = Unsafe.Add(ref b, offset + (7 * stride) + 1);

        // Columns of the 4x4 state.
        v0 = BlaMka(v0, v4); v12 = BitOperations.RotateRight(v12 ^ v0, 32);
        v8 = BlaMka(v8, v12); v4 = BitOperations.RotateRight(v4 ^ v8, 24);
        v0 = BlaMka(v0, v4); v12 = BitOperations.RotateRight(v12 ^ v0, 16);
        v8 = BlaMka(v8, v12); v4 = BitOperations.RotateRight(v4 ^ v8, 63);

        v1 = BlaMka(v1, v5); v13 = BitOperations.RotateRight(v13 ^ v1, 32);
        v9 = BlaMka(v9, v13); v5 = BitOperations.RotateRight(v5 ^ v9, 24);
        v1 = BlaMka(v1, v5); v13 = BitOperations.RotateRight(v13 ^ v1, 16);
        v9 = BlaMka(v9, v13); v5 = BitOperations.RotateRight(v5 ^ v9, 63);

        v2 = BlaMka(v2, v6); v14 = BitOperations.RotateRight(v14 ^ v2, 32);
        v10 = BlaMka(v10, v14); v6 = BitOperations.RotateRight(v6 ^ v10, 24);
        v2 = BlaMka(v2, v6); v14 = BitOperations.RotateRight(v14 ^ v2, 16);
        v10 = BlaMka(v10, v14); v6 = BitOperations.RotateRight(v6 ^ v10, 63);

        v3 = BlaMka(v3, v7); v15 = BitOperations.RotateRight(v15 ^ v3, 32);
        v11 = BlaMka(v11, v15); v7 = BitOperations.RotateRight(v7 ^ v11, 24);
        v3 = BlaMka(v3, v7); v15 = BitOperations.RotateRight(v15 ^ v3, 16);
        v11 = BlaMka(v11, v15); v7 = BitOperations.RotateRight(v7 ^ v11, 63);

        // Diagonals of the 4x4 state.
        v0 = BlaMka(v0, v5); v15 = BitOperations.RotateRight(v15 ^ v0, 32);
        v10 = BlaMka(v10, v15); v5 = BitOperations.RotateRight(v5 ^ v10, 24);
        v0 = BlaMka(v0, v5); v15 = BitOperations.RotateRight(v15 ^ v0, 16);
        v10 = BlaMka(v10, v15); v5 = BitOperations.RotateRight(v5 ^ v10, 63);

        v1 = BlaMka(v1, v6); v12 = BitOperations.RotateRight(v12 ^ v1, 32);
        v11 = BlaMka(v11, v12); v6 = BitOperations.RotateRight(v6 ^ v11, 24);
        v1 = BlaMka(v1, v6); v12 = BitOperations.RotateRight(v12 ^ v1, 16);
        v11 = BlaMka(v11, v12); v6 = BitOperations.RotateRight(v6 ^ v11, 63);

        v2 = BlaMka(v2, v7); v13 = BitOperations.RotateRight(v13 ^ v2, 32);
        v8 = BlaMka(v8, v13); v7 = BitOperations.RotateRight(v7 ^ v8, 24);
        v2 = BlaMka(v2, v7); v13 = BitOperations.RotateRight(v13 ^ v2, 16);
        v8 = BlaMka(v8, v13); v7 = BitOperations.RotateRight(v7 ^ v8, 63);

        v3 = BlaMka(v3, v4); v14 = BitOperations.RotateRight(v14 ^ v3, 32);
        v9 = BlaMka(v9, v14); v4 = BitOperations.RotateRight(v4 ^ v9, 24);
        v3 = BlaMka(v3, v4); v14 = BitOperations.RotateRight(v14 ^ v3, 16);
        v9 = BlaMka(v9, v14); v4 = BitOperations.RotateRight(v4 ^ v9, 63);

        Unsafe.Add(ref b, offset) = v0;
        Unsafe.Add(ref b, offset + 1) = v1;
        Unsafe.Add(ref b, offset + stride) = v2;
        Unsafe.Add(ref b, offset + stride + 1) = v3;
        Unsafe.Add(ref b, offset + (2 * stride)) = v4;
        Unsafe.Add(ref b, offset + (2 * stride) + 1) = v5;
        Unsafe.Add(ref b, offset + (3 * stride)) = v6;
        Unsafe.Add(ref b, offset + (3 * stride) + 1) = v7;
        Unsafe.Add(ref b, offset + (4 * stride)) = v8;
        Unsafe.Add(ref b, offset + (4 * stride) + 1) = v9;
        Unsafe.Add(ref b, offset + (5 * stride)) = v10;
        Unsafe.Add(ref b, offset + (5 * stride) + 1) = v11;
        Unsafe.Add(ref b, offset + (6 * stride)) = v12;
        Unsafe.Add(ref b, offset + (6 * stride) + 1) = v13;
        Unsafe.Add(ref b, offset + (7 * stride)) = v14;
        Unsafe.Add(ref b, offset + (7 * stride) + 1) = v15;
    }

    /// <summary>The BlaMka mixing step: <c>x + y + 2 * lower32(x) * lower32(y)</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong BlaMka(ulong x, ulong y) => x + y + (((ulong)(uint)x * (uint)y) << 1);

    /// <summary>Computes the 64-byte prehash H0 of RFC 9106 section 3.2.</summary>
    private static void ComputeH0(Argon2Type type, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
                                  ReadOnlySpan<byte> secret, ReadOnlySpan<byte> associatedData,
                                  uint memoryKiB, uint iterations, uint lanes, int tagLength, Span<byte> h0)
    {
        Blake2bHasher hasher = new(64, default);
        try
        {
            UpdateUInt32(ref hasher, lanes);
            UpdateUInt32(ref hasher, (uint)tagLength);
            UpdateUInt32(ref hasher, memoryKiB);
            UpdateUInt32(ref hasher, iterations);
            UpdateUInt32(ref hasher, Version);
            UpdateUInt32(ref hasher, (uint)type);
            UpdateUInt32(ref hasher, (uint)password.Length);
            hasher.Update(password);
            UpdateUInt32(ref hasher, (uint)salt.Length);
            hasher.Update(salt);
            UpdateUInt32(ref hasher, (uint)secret.Length);
            hasher.Update(secret);
            UpdateUInt32(ref hasher, (uint)associatedData.Length);
            hasher.Update(associatedData);
            hasher.Final(h0);
        }
        finally
        {
            hasher.Clear();
        }
    }

    private static void UpdateUInt32(ref Blake2bHasher hasher, uint value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        hasher.Update(scratch);
    }

    /// <summary>The variable-length hash H' of RFC 9106 section 3.3.</summary>
    private static void VariableHash(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> lengthPrefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)output.Length);

        if (output.Length <= Blake2bHasher.MaxDigestLength)
        {
            Blake2bHasher direct = new(output.Length, default);
            try
            {
                direct.Update(lengthPrefix);
                direct.Update(input);
                direct.Final(output);
            }
            finally
            {
                direct.Clear();
            }

            return;
        }

        Span<byte> v = stackalloc byte[Blake2bHasher.MaxDigestLength];
        try
        {
            Blake2bHasher first = new(Blake2bHasher.MaxDigestLength, default);
            try
            {
                first.Update(lengthPrefix);
                first.Update(input);
                first.Final(v);
            }
            finally
            {
                first.Clear();
            }

            v[..32].CopyTo(output);
            int produced = 32;

            while (output.Length - produced > Blake2bHasher.MaxDigestLength)
            {
                Blake2bHasher step = new(Blake2bHasher.MaxDigestLength, default);
                try
                {
                    step.Update(v);
                    step.Final(v);
                }
                finally
                {
                    step.Clear();
                }

                v[..32].CopyTo(output[produced..]);
                produced += 32;
            }

            Blake2bHasher last = new(output.Length - produced, default);
            try
            {
                last.Update(v);
                last.Final(output[produced..]);
            }
            finally
            {
                last.Clear();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(v);
        }
    }

    /// <summary>Xors the last block of every lane together and runs H' over it (RFC 9106 section 3.2 step 7).</summary>
    private static byte[] Finalize(ulong[] memory, uint lanes, uint laneLength, int tagLength)
    {
        Span<ulong> accumulator = stackalloc ulong[WordsPerBlock];
        memory.AsSpan((int)((long)(laneLength - 1) * WordsPerBlock), WordsPerBlock).CopyTo(accumulator);

        for (uint lane = 1; lane < lanes; lane++)
        {
            ReadOnlySpan<ulong> last = memory.AsSpan((int)((long)((lane * laneLength) + laneLength - 1) * WordsPerBlock), WordsPerBlock);
            for (int i = 0; i < WordsPerBlock; i++)
            {
                accumulator[i] ^= last[i];
            }
        }

        byte[] tag = GC.AllocateArray<byte>(tagLength, pinned: true);
        try
        {
            VariableHash(MemoryMarshal.AsBytes(accumulator), tag);
        }
        finally
        {
            accumulator.Clear();
        }

        return tag;
    }

    private static Span<byte> BlockBytes(ulong[] memory, uint blockIndex) =>
        MemoryMarshal.AsBytes(memory.AsSpan((int)((long)blockIndex * WordsPerBlock), WordsPerBlock));
}
