using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.EndToEnd;

/// <summary>
/// The video preview over the shipping stack (#33): a real vault, the real seekable decrypting
/// stream and the real Media Foundation thumbnailer, once while the file is still staged and once
/// after it is stored in the vault file. Nothing is written to disk but the vault itself.
/// </summary>
public sealed class VideoPreviewEndToEndTests : IDisposable
{
    private static readonly KdfParameters TestKdf = new(8192, 1, 1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "BastionE2E", Guid.NewGuid().ToString("N"));
    private readonly MemoryLog _log = new();
    private readonly InlineDispatcher _dispatcher = new();

    public VideoPreviewEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp directory is per test.
        }
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny-h264.mp4");

    private ExplorerViewModel NewExplorer(IVaultSession session)
    {
        var files = Substitute.For<IFileDialogService>();
        files.PickFilesToImport().Returns([]);

        return new ExplorerViewModel(
            session,
            Substitute.For<IDialogService>(),
            files,
            new InternalClipboard(),
            Substitute.For<IOsClipboard>(),
            new MemorySettings(),
            _dispatcher,
            _log,
            new OperationViewModel(_dispatcher, _log),
            new MediaFoundationThumbnailer(_log));
    }

    private static async Task AssertPreviewShowsTheClipAsync(ExplorerViewModel explorer, IVaultSession session)
    {
        EntryInfo clip = Assert.Single(session.GetChildren(EntryId.Root));
        var item = new EntryItemViewModel(clip, "/" + clip.Name);

        PreviewViewModel preview = explorer.Preview;
        preview.Debounce = TimeSpan.Zero;
        preview.Show(item);
        await preview.Completion;

        Assert.Equal(PreviewMode.Video, preview.Mode);
        Assert.NotNull(preview.VideoFrame);
        Assert.Equal(160, preview.VideoFrame.Width);
        Assert.Equal(90, preview.VideoFrame.Height);
        Assert.Contains(preview.VideoFrame.Pixels, b => b != 0);
        Assert.Null(preview.Message);
        Assert.StartsWith("MP4 video · 160×90 · 0:02 · ", preview.InstrumentLine, StringComparison.Ordinal);

        preview.Show(null);
        Assert.Null(preview.VideoFrame);
    }

    [Fact]
    public async Task A_video_in_a_real_vault_previews_while_staged_and_again_once_stored()
    {
        if (!MediaFoundationThumbnailer.IsAvailable)
        {
            return;
        }

        string vaultPath = Path.Combine(_root, "clips.bastion");
        var factory = new VaultFactory();

        using Passphrase password = Passphrase.FromString("correct horse battery staple");
        await using IVaultSession session = await factory.CreateAsync(vaultPath, password, null, TestKdf, null, CancellationToken.None);
        await session.ImportAsync(EntryId.Root, [Fixture], new ImportOptions(), null, CancellationToken.None);

        using (ExplorerViewModel staged = NewExplorer(session))
        {
            await AssertPreviewShowsTheClipAsync(staged, session);
        }

        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.Equal(EntryState.Stored, Assert.Single(session.GetChildren(EntryId.Root)).State);

        using (ExplorerViewModel stored = NewExplorer(session))
        {
            await AssertPreviewShowsTheClipAsync(stored, session);
        }

        Assert.DoesNotContain(_log.Lines, line => line.StartsWith("WRN", StringComparison.Ordinal) || line.StartsWith("ERR", StringComparison.Ordinal));
        Assert.Equal([vaultPath], Directory.GetFiles(_root));
    }
}
