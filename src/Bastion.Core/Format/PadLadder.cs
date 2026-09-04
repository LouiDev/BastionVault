using System.Numerics;

namespace Bastion.Core.Format;

/// <summary>The padding schedules that hide the exact index and data sizes.</summary>
public static class PadLadder
{
    /// <summary>The floor of the index ladder and the size of the smallest padded index (64 KiB).</summary>
    private const long IndexFloor = 65536;

    /// <summary>One mebibyte; the top of the power-of-two step and the granularity above it.</summary>
    private const long OneMiB = 1024 * 1024;

    /// <summary>
    /// Index padding (FORMAT.md section 4.2): 65536 for anything up to 64 KiB, then the next power of
    /// two up to 1 MiB, then the next multiple of 1 MiB.
    /// </summary>
    /// <param name="unpaddedLength">Serialized index length in bytes.</param>
    /// <returns>The padded plaintext length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="unpaddedLength"/> is negative or too large to pad.</exception>
    public static long Index(long unpaddedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unpaddedLength);

        if (unpaddedLength <= IndexFloor)
        {
            return IndexFloor;
        }

        if (unpaddedLength <= OneMiB)
        {
            return (long)BitOperations.RoundUpToPowerOf2((ulong)unpaddedLength);
        }

        return RoundUpToMultiple(unpaddedLength, OneMiB, nameof(unpaddedLength));
    }

    /// <summary>
    /// Data section padding for size obfuscation (FORMAT.md section 5): the next multiple of
    /// <c>max(1 MiB, 2^floor(log2 n) / 16)</c>. A writer choice; readers do not depend on it.
    /// </summary>
    /// <param name="dataLength">Sum of all blob lengths in bytes.</param>
    /// <returns>The padded data section length; 0 for an empty data section.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dataLength"/> is negative or too large to pad.</exception>
    public static long Obfuscation(long dataLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataLength);

        if (dataLength == 0)
        {
            return 0;
        }

        long highestPowerOfTwo = 1L << BitOperations.Log2((ulong)dataLength);
        long step = Math.Max(OneMiB, highestPowerOfTwo / 16);
        return RoundUpToMultiple(dataLength, step, nameof(dataLength));
    }

    /// <summary>Rounds a non-negative value up to the next multiple of <paramref name="step"/>.</summary>
    /// <param name="value">Value to round.</param>
    /// <param name="step">Positive granularity.</param>
    /// <param name="paramName">Name of the public parameter that carried the value.</param>
    private static long RoundUpToMultiple(long value, long step, string paramName)
    {
        if (value > long.MaxValue - (step - 1))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"The value cannot be rounded up to the next multiple of {step} without overflowing.");
        }

        return (value + step - 1) / step * step;
    }
}
