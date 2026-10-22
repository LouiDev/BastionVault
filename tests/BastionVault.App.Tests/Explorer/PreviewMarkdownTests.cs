using System.Text;
using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The preview pane's Markdown path: a <c>.md</c> file is read like text, handed to the view as source
/// for rendering, and dropped with the other buffers. Bytes that are not UTF-8 fall back to the hex
/// dump exactly as a text file would.
/// </summary>
public sealed class PreviewMarkdownTests
{
    private readonly IVaultSession _session = Substitute.For<IVaultSession>();

    private PreviewViewModel NewPreview(byte[] bytes)
    {
        _session.OpenReadAsync(Arg.Any<EntryId>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes)));

        return new PreviewViewModel(_session, new MemorySettings(), new FakeVideoThumbnailer(), new FakeAudioProber(), new MemoryLog())
        {
            Debounce = TimeSpan.Zero,
        };
    }

    private static EntryItemViewModel Item(string name, long length) => new(
        new EntryInfo(new EntryId(7), EntryId.Root, EntryKind.File, name, length, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, string.Empty, EntryState.Stored),
        "/" + name);

    [Fact]
    public async Task A_markdown_file_is_handed_to_the_view_as_source()
    {
        const string source = "# Title\n\nSome *text* with `code`.\n";
        byte[] bytes = Encoding.UTF8.GetBytes(source);

        PreviewViewModel preview = NewPreview(bytes);
        preview.Show(Item("Notes.md", bytes.Length));
        await preview.Completion;

        Assert.Equal(PreviewMode.Markdown, preview.Mode);
        Assert.Equal(source, preview.Markdown);
        Assert.Null(preview.Text);
        Assert.StartsWith("Markdown document · ", preview.InstrumentLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_byte_order_mark_is_not_part_of_the_source()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("plain")];

        PreviewViewModel preview = NewPreview(bytes);
        preview.Show(Item("Notes.md", bytes.Length));
        await preview.Completion;

        Assert.Equal("plain", preview.Markdown);
    }

    [Fact]
    public async Task Bytes_that_are_not_utf8_fall_back_to_the_hex_dump()
    {
        byte[] bytes = [0x23, 0x20, 0xFF, 0xFE, 0xC0];

        PreviewViewModel preview = NewPreview(bytes);
        preview.Show(Item("Notes.md", bytes.Length));
        await preview.Completion;

        Assert.Equal(PreviewMode.Hex, preview.Mode);
        Assert.Null(preview.Markdown);
        Assert.NotNull(preview.Text);
    }

    [Fact]
    public async Task A_markdown_file_over_the_text_cap_is_refused_before_it_is_read()
    {
        PreviewViewModel preview = NewPreview([]);
        preview.Show(Item("Huge.md", PreviewViewModel.MaxTextBytes + 1));
        await preview.Completion;

        Assert.Equal(PreviewMode.TooLarge, preview.Mode);
        await _session.DidNotReceive().OpenReadAsync(Arg.Any<EntryId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Moving_the_selection_on_drops_the_source()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("# Title");

        PreviewViewModel preview = NewPreview(bytes);
        preview.Show(Item("Notes.md", bytes.Length));
        await preview.Completion;
        Assert.NotNull(preview.Markdown);

        preview.Show(null);

        Assert.Null(preview.Markdown);
        Assert.Equal(PreviewMode.Empty, preview.Mode);
    }

    [Fact]
    public async Task A_rendered_document_is_blurred_when_the_window_is_inactive()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("# Title");

        PreviewViewModel preview = NewPreview(bytes);
        preview.Show(Item("Notes.md", bytes.Length));
        await preview.Completion;

        Assert.False(preview.IsBlurred);
        preview.IsWindowActive = false;
        Assert.True(preview.IsBlurred);
    }
}
