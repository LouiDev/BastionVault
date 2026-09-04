using BastionVault.App.Services;
using BastionVault.Core;

namespace BastionVault.App.Tests.Explorer;

/// <summary>
/// The extension table. It has to answer for every name without ever asking the Windows shell,
/// because an in-vault name never leaves the process.
/// </summary>
public sealed class FileTypeCatalogTests
{
    [Theory]
    [InlineData("Passport scan.pdf", "PDF document", "Glyph.Document", PreviewKind.Binary)]
    [InlineData("Notes.txt", "Text document", "Glyph.Document", PreviewKind.Text)]
    [InlineData("Family portrait.jpg", "JPEG image", "Glyph.Image", PreviewKind.Image)]
    [InlineData("backup.tar", "Tar archive", "Glyph.Archive", PreviewKind.Binary)]
    [InlineData("clip.mp4", "MP4 video", "Glyph.Video", PreviewKind.Binary)]
    [InlineData("song.flac", "FLAC audio", "Glyph.Audio", PreviewKind.Binary)]
    [InlineData("signing.pfx", "PKCS 12 store", "Glyph.KeyFile", PreviewKind.Binary)]
    [InlineData("Program.cs", "C# source file", "Glyph.Code", PreviewKind.Text)]
    public void KnownExtensionsAreDescribed(string name, string type, string glyph, PreviewKind preview)
    {
        FileTypeInfo info = FileTypeCatalog.Describe(name);

        Assert.Equal(type, info.FriendlyType);
        Assert.Equal(glyph, info.GlyphKey);
        Assert.Equal(preview, info.Preview);
    }

    [Fact]
    public void TheTableIsCaseInsensitive()
    {
        Assert.Equal("PDF document", FileTypeCatalog.Describe("SCAN.PDF").FriendlyType);
    }

    [Fact]
    public void AnUnknownExtensionBecomesItsOwnType()
    {
        FileTypeInfo info = FileTypeCatalog.Describe("archive.qqq");

        Assert.Equal("QQQ file", info.FriendlyType);
        Assert.Equal("Glyph.File", info.GlyphKey);
        Assert.Equal(PreviewKind.Binary, info.Preview);
    }

    [Fact]
    public void AWholeNameCanBeKnownWithoutAnExtension()
    {
        Assert.Equal("Private key", FileTypeCatalog.Describe("ssh_ed25519").FriendlyType);
        Assert.Equal("Public key", FileTypeCatalog.Describe("ssh_ed25519.pub").FriendlyType);
    }

    [Fact]
    public void ANamelessOrExtensionlessEntryIsJustAFile()
    {
        Assert.Equal("File", FileTypeCatalog.Describe("payload").FriendlyType);
        Assert.Equal("File", FileTypeCatalog.Describe(string.Empty).FriendlyType);
        Assert.Equal("File", FileTypeCatalog.Describe((string?)null).FriendlyType);
    }

    [Fact]
    public void AFolderIsAlwaysAFolder()
    {
        FileTypeInfo info = FileTypeCatalog.Describe(EntryKind.Folder, "Documents.pdf");

        Assert.Equal("Folder", info.FriendlyType);
        Assert.Equal("Glyph.Folder", info.GlyphKey);
    }

    [Theory]
    [InlineData("report.pdf", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("noextension", "")]
    [InlineData(".gitignore", "")]
    [InlineData("trailing.", "")]
    public void ExtensionsAreReadOffTheLastDot(string name, string extension)
    {
        Assert.Equal(extension, FileTypeCatalog.Extension(name));
    }

    [Theory]
    [InlineData("report.pdf", 6)]
    [InlineData("archive.tar.gz", 11)]
    [InlineData("noextension", 11)]
    [InlineData(".gitignore", 10)]
    public void RenameSelectsTheStemWithoutTheExtension(string name, int stem)
    {
        Assert.Equal(stem, FileTypeCatalog.StemLength(name));
    }
}
