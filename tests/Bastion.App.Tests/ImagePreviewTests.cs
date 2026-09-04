using System.Windows;
using System.Windows.Threading;
using Bastion.App.Views;

namespace Bastion.App.Tests;

/// <summary>
/// The preview pane decodes bytes that came out of a vault, so the file and its password may both
/// be the attacker's. A decode must therefore never throw out of <see cref="ImagePreview"/>: the
/// same bytes are handed back to WIC on every pane resize through the DecodePixelWidth binding,
/// and nothing on that path catches anything - an escape reaches the dispatcher's crash handler,
/// which zeroes the vault keys and shuts the app down with the user's unsaved edits in it.
/// </summary>
public sealed class ImagePreviewTests
{
    /// <summary>A GIF header followed by four 0xFF bytes: WIC raises IOException on this one.</summary>
    private static byte[] TruncatedGif =>
        [.. "GIF89a"u8.ToArray(), 0xFF, 0xFF, 0xFF, 0xFF];

    [Fact]
    public void AMalformedImageBecomesAFailureAndSurvivesAResize()
    {
        (string? failure, object? source, Exception? escaped) = OnStaThread(() =>
        {
            var preview = new ImagePreview();

            // 1. The first assignment must not throw.
            preview.Bytes = TruncatedGif;

            // 2. Nor must the width push the pane makes on every SizeChanged.
            preview.DecodePixelWidth = 512;
            preview.DecodePixelWidth = 1024;

            return (preview.Failure, preview.Source, null);
        });

        Assert.Null(escaped);
        Assert.NotNull(failure);
        Assert.Null(source);
    }

    [Fact]
    public void ClearingTheBytesClearsTheFailure()
    {
        (string? failure, object? _, Exception? escaped) = OnStaThread(() =>
        {
            var preview = new ImagePreview { Bytes = TruncatedGif };
            preview.Bytes = null;
            return (preview.Failure, preview.Source, null);
        });

        Assert.Null(escaped);
        Assert.Null(failure);
    }

    [Fact]
    public void ARealImageStillDecodes()
    {
        (string? failure, object? source, Exception? escaped) = OnStaThread(() =>
        {
            var preview = new ImagePreview { Bytes = OnePixelPng() };
            preview.DecodePixelWidth = 64;
            return (preview.Failure, preview.Source, null);
        });

        Assert.Null(escaped);
        Assert.Null(failure);
        Assert.NotNull(source);
    }

    /// <summary>A valid 1x1 PNG, so the happy path is covered by the same harness.</summary>
    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>
    /// Runs a probe on a fresh STA thread with its own dispatcher. Anything the probe throws is
    /// returned rather than rethrown, so a test can assert that nothing escaped.
    /// </summary>
    /// <param name="probe">Work to run.</param>
    private static (string? Failure, object? Source, Exception? Escaped) OnStaThread(
        Func<(string?, object?, Exception?)> probe)
    {
        (string?, object?, Exception?) result = (null, null, null);

        var thread = new Thread(() =>
        {
            try
            {
                result = probe();
            }
            catch (Exception ex)
            {
                result = (null, null, ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the STA probe did not finish");

        return result;
    }
}
