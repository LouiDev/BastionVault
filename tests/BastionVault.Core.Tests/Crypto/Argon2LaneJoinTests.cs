using BastionVault.Core.Crypto;

namespace BastionVault.Core.Tests.Crypto;

/// <summary>
/// The lane join of <c>Argon2.FillSlice</c>: lane 0 runs inline and lanes 1..p-1 on the thread pool. When
/// lane 0 throws, the background lanes must still be joined before the exception leaves, because the
/// <c>finally</c> in <c>Derive</c> zeroes the block array and an orphaned lane would write password-derived
/// state back into it afterwards. The 1.0 review fixed this without a test; the segment hook is the seam
/// that makes the failure injectable.
/// </summary>
public sealed class Argon2LaneJoinTests
{
    private const uint MemoryKiB = 32;
    private const uint Passes = 3;
    private const uint Lanes = 4;

    private static byte[] Password => Filled(32, 0x01);

    private static byte[] Salt => Filled(16, 0x02);

    [Fact]
    public void When_lane_zero_throws_every_other_lane_is_joined_first_and_the_memory_is_zeroed()
    {
        int backgroundLanesFinished = 0;
        ulong[]? observed = null;
        var injected = new InvalidOperationException("lane 0 failed on purpose");

        void Hook(ulong[] memory, uint pass, uint slice, uint lane)
        {
            Volatile.Write(ref observed, memory);
            if (pass != 1 || slice != 2)
            {
                return;
            }

            if (lane == 0)
            {
                // The background lanes of this slice were started a moment ago and are still running.
                throw injected;
            }

            // A background lane that is slow to finish: the join must wait for it.
            Thread.Sleep(40);
            Interlocked.Increment(ref backgroundLanesFinished);
        }

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            Argon2.HashWithSegmentHook(Argon2Type.Id, Password, Salt, MemoryKiB, Passes, Lanes, 32, Hook, CancellationToken.None));

        // The inline lane's own exception, not an AggregateException wrapping the join.
        Assert.Same(injected, thrown);

        // Every background lane of the failing slice had finished by the time the exception surfaced.
        Assert.Equal((int)Lanes - 1, Volatile.Read(ref backgroundLanesFinished));

        // And the pinned block array holds nothing of the derivation any more.
        Assert.NotNull(observed);
        Assert.All(observed, word => Assert.Equal(0UL, word));
    }

    [Fact]
    public void When_a_background_lane_throws_the_failure_propagates_and_the_memory_is_zeroed()
    {
        ulong[]? observed = null;
        var injected = new InvalidOperationException("lane 2 failed on purpose");

        void Hook(ulong[] memory, uint pass, uint slice, uint lane)
        {
            Volatile.Write(ref observed, memory);
            if (pass == 0 && slice == 3 && lane == 2)
            {
                throw injected;
            }
        }

        Exception thrown = Assert.ThrowsAny<Exception>(() =>
            Argon2.HashWithSegmentHook(Argon2Type.Id, Password, Salt, MemoryKiB, Passes, Lanes, 32, Hook, CancellationToken.None));

        Exception root = thrown is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : thrown;
        Assert.Same(injected, root);
        Assert.NotNull(observed);
        Assert.All(observed, word => Assert.Equal(0UL, word));
    }

    [Fact]
    public void The_hook_changes_no_byte_of_the_derivation()
    {
        int segments = 0;
        var seen = new HashSet<(uint Pass, uint Slice, uint Lane)>();

        void Hook(ulong[] memory, uint pass, uint slice, uint lane)
        {
            Interlocked.Increment(ref segments);
            lock (seen)
            {
                seen.Add((pass, slice, lane));
            }
        }

        byte[] plain = Argon2.Hash(Argon2Type.Id, Password, Salt, default, default, MemoryKiB, Passes, Lanes, 32, CancellationToken.None);
        byte[] hooked = Argon2.HashWithSegmentHook(Argon2Type.Id, Password, Salt, MemoryKiB, Passes, Lanes, 32, Hook, CancellationToken.None);

        Assert.Equal(plain, hooked);

        // Every (pass, slice, lane) segment was visited exactly once.
        Assert.Equal((int)(Passes * 4 * Lanes), segments);
        Assert.Equal((int)(Passes * 4 * Lanes), seen.Count);
    }

    private static byte[] Filled(int length, byte value)
    {
        byte[] buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }
}
