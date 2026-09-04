using Bastion.App.Services;
using Bastion.App.Tests.Fakes;
using Bastion.Core;

namespace Bastion.App.Tests;

/// <summary>
/// <see cref="ThrottledProgress{T}"/> exists so a fast operation cannot flood the dispatcher.
/// These tests pin the two properties that matter: at most one pending callback, and the value
/// that arrives is always the newest one.
/// </summary>
public sealed class ThrottledProgressTests
{
    [Fact]
    public void ManyReportsBeforeADrainCollapseIntoOneCallback()
    {
        var dispatcher = new ManualDispatcher();
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(dispatcher, seen.Add);

        for (int i = 1; i <= 100; i++)
        {
            progress.Report(i);
        }

        Assert.Equal(1, dispatcher.Pending);

        dispatcher.Drain();

        Assert.Equal([100], seen);
        Assert.Equal(1, progress.DeliveredCount);
    }

    [Fact]
    public void ReportAfterADrainPostsAgain()
    {
        var dispatcher = new ManualDispatcher();
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(dispatcher, seen.Add);

        progress.Report(1);
        dispatcher.Drain();
        progress.Report(2);
        dispatcher.Drain();

        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void FlushWithNothingPendingDoesNothing()
    {
        var dispatcher = new ManualDispatcher();
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(dispatcher, seen.Add);

        progress.Flush();

        Assert.Empty(seen);
        Assert.Equal(0, progress.DeliveredCount);
    }

    [Fact]
    public void TheNewestVaultProgressWins()
    {
        var dispatcher = new ManualDispatcher();
        VaultProgress? last = null;
        var progress = new ThrottledProgress<VaultProgress>(dispatcher, p => last = p);

        progress.Report(new VaultProgress(VaultOperation.Import, 10, 100, 1, 10, "a", true));
        progress.Report(new VaultProgress(VaultOperation.Import, 90, 100, 9, 10, "b", false));
        dispatcher.Drain();

        Assert.NotNull(last);
        Assert.Equal(90, last!.Value.BytesDone);
        Assert.False(last.Value.IsCancellable);
    }
}
