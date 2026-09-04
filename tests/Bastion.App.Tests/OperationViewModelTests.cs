using Bastion.App.Tests.Fakes;
using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Tests;

/// <summary>
/// The long-operation runner. What matters here is not the numbers but the promises: progress is
/// coalesced, cancel works, and a non-cancellable phase really does take the cancel button away.
/// </summary>
public sealed class OperationViewModelTests
{
    [Fact]
    public async Task RunAsyncReportsProgressAndReturnsTheResult()
    {
        var operation = NewOperation();

        int result = await operation.RunAsync(
            VaultOperation.Import,
            "Importing",
            async (progress, ct) =>
            {
                for (int i = 1; i <= 5; i++)
                {
                    progress.Report(new VaultProgress(VaultOperation.Import, i * 20, 100, i, 5, $"item {i}", true));
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }

                return 42;
            });

        Assert.Equal(42, result);
        Assert.False(operation.IsRunning);
        Assert.False(operation.WasCancelled);
        Assert.Equal(100, operation.Percent, 3);
        Assert.Equal(5, operation.ItemsDone);
        Assert.Equal("item 5", operation.CurrentItem);
    }

    [Fact]
    public async Task CancelStopsTheWorkAndIsReported()
    {
        var operation = NewOperation();
        var started = new TaskCompletionSource();

        Task<int> run = operation.RunAsync<int>(
            VaultOperation.Export,
            "Exporting",
            async (progress, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return 1;
            });

        await started.Task;
        Assert.True(operation.CancelCommand.CanExecute(null));
        operation.Cancel();

        int result = await run;

        Assert.Equal(0, result);
        Assert.True(operation.WasCancelled);
        Assert.True(operation.CancelRequested);
        Assert.False(operation.IsRunning);
    }

    [Fact]
    public async Task ANonCancellablePhaseDisablesTheCancelCommand()
    {
        var dispatcher = new InlineDispatcher();
        var operation = new OperationViewModel(dispatcher, new MemoryLog());
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Task run = operation.RunAsync(
            VaultOperation.Save,
            "Saving",
            async (progress, ct) =>
            {
                progress.Report(new VaultProgress(VaultOperation.Save, 90, 100, 9, 10, "swapping the file", false));
                reported.SetResult();
                await release.Task.ConfigureAwait(false);
            });

        await reported.Task;

        Assert.False(operation.IsCancellable);
        Assert.False(operation.CancelCommand.CanExecute(null));

        release.SetResult();
        await run;
    }

    [Fact]
    public async Task TwoOperationsAtOnceAreRefused()
    {
        var operation = NewOperation();
        var release = new TaskCompletionSource();

        Task first = operation.RunAsync(VaultOperation.Verify, "Verifying", (_, _) => release.Task);
        await WaitUntil(() => operation.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.RunAsync(VaultOperation.Verify, "Verifying again", (_, _) => Task.CompletedTask));

        release.SetResult();
        await first;
    }

    [Fact]
    public void EtaReadsAsASentence()
    {
        Assert.Equal("a few seconds left", OperationViewModel.FormatEta(TimeSpan.FromSeconds(2)));
        Assert.Equal("30 seconds left", OperationViewModel.FormatEta(TimeSpan.FromSeconds(30)));
        Assert.Equal("about a minute left", OperationViewModel.FormatEta(TimeSpan.FromSeconds(60)));
        Assert.Contains("minutes left", OperationViewModel.FormatEta(TimeSpan.FromMinutes(9)), StringComparison.Ordinal);
        Assert.Contains(" h ", OperationViewModel.FormatEta(TimeSpan.FromHours(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void FormatBytesUsesBinaryUnits()
    {
        Assert.Equal("512 B", OperationViewModel.FormatBytes(512));
        Assert.EndsWith("KB", OperationViewModel.FormatBytes(1024), StringComparison.Ordinal);
        Assert.EndsWith("MB", OperationViewModel.FormatBytes(5 * 1024 * 1024), StringComparison.Ordinal);
        Assert.EndsWith("GB", OperationViewModel.FormatBytes(3L * 1024 * 1024 * 1024), StringComparison.Ordinal);
    }

    private static OperationViewModel NewOperation() => new(new InlineDispatcher(), new MemoryLog());

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
