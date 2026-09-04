using BastionVault.Core.Crypto;
using KonsciousArgon2d = Konscious.Security.Cryptography.Argon2d;
using KonsciousArgon2i = Konscious.Security.Cryptography.Argon2i;
using KonsciousArgon2id = Konscious.Security.Cryptography.Argon2id;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>
/// Differential test of the built-in Argon2 against Konscious.Security.Cryptography.Argon2. The RFC
/// vectors pin one point of the parameter space; this pins the shape of the space itself: lane counts,
/// pass counts, memory sizes that are not a multiple of <c>4 * p</c> (so the <c>m' = m - m mod 4p</c>
/// truncation is exercised) and tag lengths above 64 bytes (so the multi-block H' of RFC 9106 section
/// 3.3 is compared with a reference). The vault format can exercise neither of those last two itself:
/// <c>KdfParameters.Validate</c> forces a multiple of <c>4 * p</c> and the vault always asks for 32
/// bytes, so a regression there would otherwise go unnoticed.
/// </summary>
public sealed class Argon2DifferentialTests
{
    /// <summary>Number of random parameter sets; kept small so the crypto suite stays fast.</summary>
    private const int Cases = 20;

    /// <summary>Fixed seed so a failure is reproducible.</summary>
    private const int Seed = 20260902;

    [Fact]
    public void Argon2id_MatchesKonsciousOnRandomParameterSets()
    {
        Random random = new(Seed);

        for (int i = 0; i < Cases; i++)
        {
            (byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism) = NextCase(random);

            byte[] mine = Argon2.Hash(
                Argon2Type.Id, password, salt, default, default,
                (uint)memoryKiB, (uint)iterations, (uint)parallelism, 32, CancellationToken.None);

            using KonsciousArgon2id reference = new(password)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };

            Assert.Equal(
                Convert.ToHexStringLower(reference.GetBytes(32)),
                Convert.ToHexStringLower(mine));
        }
    }

    /// <summary>The same comparison for the two pure variants, so the shared core is covered end to end.</summary>
    [Fact]
    public void Argon2dAndArgon2i_MatchKonscious()
    {
        Random random = new(Seed + 1);

        for (int i = 0; i < 4; i++)
        {
            (byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism) = NextCase(random);

            byte[] mineD = Argon2.Hash(
                Argon2Type.D, password, salt, default, default,
                (uint)memoryKiB, (uint)iterations, (uint)parallelism, 32, CancellationToken.None);
            using (KonsciousArgon2d referenceD = new(password)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            })
            {
                Assert.Equal(Convert.ToHexStringLower(referenceD.GetBytes(32)), Convert.ToHexStringLower(mineD));
            }

            byte[] mineI = Argon2.Hash(
                Argon2Type.I, password, salt, default, default,
                (uint)memoryKiB, (uint)iterations, (uint)parallelism, 32, CancellationToken.None);
            using (KonsciousArgon2i referenceI = new(password)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            })
            {
                Assert.Equal(Convert.ToHexStringLower(referenceI.GetBytes(32)), Convert.ToHexStringLower(mineI));
            }
        }
    }

    /// <summary>Secret and associated data are covered too; they only enter through H0.</summary>
    [Fact]
    public void Argon2id_WithSecretAndAssociatedData_MatchesKonscious()
    {
        Random random = new(Seed + 2);
        (byte[] password, byte[] salt, int memoryKiB, int iterations, int parallelism) = NextCase(random);

        byte[] secret = new byte[16];
        byte[] associatedData = new byte[24];
        random.NextBytes(secret);
        random.NextBytes(associatedData);

        byte[] mine = Argon2.Hash(
            Argon2Type.Id, password, salt, secret, associatedData,
            (uint)memoryKiB, (uint)iterations, (uint)parallelism, 32, CancellationToken.None);

        using KonsciousArgon2id reference = new(password)
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
            KnownSecret = secret,
            AssociatedData = associatedData,
        };

        Assert.Equal(Convert.ToHexStringLower(reference.GetBytes(32)), Convert.ToHexStringLower(mine));
    }

    /// <summary>
    /// The <c>m' = m - m mod 4p</c> truncation of RFC 9106 section 3.2, which the vault's own parameter
    /// validation never reaches because it requires a multiple of <c>4 * p</c>.
    /// </summary>
    /// <param name="memoryKiB">Requested memory, deliberately not a multiple of <c>4 * p</c>.</param>
    /// <param name="parallelism">Lane count.</param>
    [Theory]
    [InlineData(8193, 1)]
    [InlineData(8194, 2)]
    [InlineData(8195, 3)]
    [InlineData(8199, 4)]
    [InlineData(8203, 6)]
    [InlineData(9001, 8)]
    [InlineData(12345, 16)]
    public void Argon2id_MatchesKonsciousWhenMemoryIsNotAMultipleOfTheSegmentLength(int memoryKiB, int parallelism)
    {
        Assert.NotEqual(0, memoryKiB % (4 * parallelism));

        byte[] password = "differential"u8.ToArray();
        byte[] salt = new byte[16];
        salt[0] = (byte)memoryKiB;

        byte[] mine = Argon2.Hash(
            Argon2Type.Id, password, salt, default, default,
            (uint)memoryKiB, 1, (uint)parallelism, 32, CancellationToken.None);

        using KonsciousArgon2id reference = new(password)
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = 1,
            DegreeOfParallelism = parallelism,
        };

        Assert.Equal(Convert.ToHexStringLower(reference.GetBytes(32)), Convert.ToHexStringLower(mine));
    }

    /// <summary>
    /// The variable-length hash H' of RFC 9106 section 3.3 only leaves its single-block path above 64
    /// bytes; the vault always asks for 32, so nothing else covers the block chain.
    /// </summary>
    /// <param name="tagLength">Requested tag length in bytes.</param>
    [Theory]
    [InlineData(65)]
    [InlineData(96)]
    [InlineData(100)]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(1024)]
    public void Argon2id_MatchesKonsciousForTagsLongerThanOneBlakeBlock(int tagLength)
    {
        byte[] password = "long tag"u8.ToArray();
        byte[] salt = new byte[16];
        salt[0] = (byte)tagLength;

        byte[] mine = Argon2.Hash(
            Argon2Type.Id, password, salt, default, default,
            8192, 1, 2, tagLength, CancellationToken.None);

        using KonsciousArgon2id reference = new(password)
        {
            Salt = salt,
            MemorySize = 8192,
            Iterations = 1,
            DegreeOfParallelism = 2,
        };

        Assert.Equal(Convert.ToHexStringLower(reference.GetBytes(tagLength)), Convert.ToHexStringLower(mine));
    }

    /// <summary>
    /// Draws one parameter set: memory 8 .. 64 MiB rounded down to a multiple of <c>4 * p</c>,
    /// 1 .. 3 passes and 1 .. 4 lanes.
    /// </summary>
    private static (byte[] Password, byte[] Salt, int MemoryKiB, int Iterations, int Parallelism) NextCase(Random random)
    {
        int parallelism = random.Next(1, 5);
        int step = 4 * parallelism;
        int memoryKiB = random.Next(8 * 1024, (64 * 1024) + 1);
        memoryKiB -= memoryKiB % step;
        int iterations = random.Next(1, 4);

        byte[] password = new byte[random.Next(1, 65)];
        byte[] salt = new byte[random.Next(8, 33)];
        random.NextBytes(password);
        random.NextBytes(salt);

        return (password, salt, memoryKiB, iterations, parallelism);
    }
}
