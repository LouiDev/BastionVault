using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>How the preview pane can render a file, decided from its name alone.</summary>
public enum PreviewKind
{
    /// <summary>Decode as UTF-8 text and show it.</summary>
    Text,

    /// <summary>Hand the bytes to an image decoder.</summary>
    Image,

    /// <summary>Nothing better than a hex dump of the first bytes.</summary>
    Binary,
}

/// <summary>What the catalog knows about one extension.</summary>
/// <param name="FriendlyType">The Type column's text, for example "PDF document".</param>
/// <param name="GlyphKey">Resource key of the 16 px type icon.</param>
/// <param name="Preview">How the preview pane should try to render the file.</param>
public sealed record FileTypeInfo(string FriendlyType, string GlyphKey, PreviewKind Preview);

/// <summary>
/// Extension to friendly type name, icon and preview strategy. The table is fixed and lives in
/// this process: asking the Windows shell would mean handing an in-vault name to
/// <c>SHGetFileInfo</c>, and the names inside a vault never leave the app (THREAT-MODEL.md).
/// </summary>
public static class FileTypeCatalog
{
    private const string GlyphFolder = "Glyph.Folder";
    private const string GlyphFile = "Glyph.File";
    private const string GlyphDocument = "Glyph.Document";
    private const string GlyphImage = "Glyph.Image";
    private const string GlyphCode = "Glyph.Code";
    private const string GlyphArchive = "Glyph.Archive";
    private const string GlyphVideo = "Glyph.Video";
    private const string GlyphAudio = "Glyph.Audio";
    private const string GlyphKeyFile = "Glyph.KeyFile";

    private static readonly FileTypeInfo Unknown = new("File", GlyphFile, PreviewKind.Binary);

    private static readonly Dictionary<string, FileTypeInfo> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Documents
        ["pdf"] = new("PDF document", GlyphDocument, PreviewKind.Binary),
        ["doc"] = new("Word document", GlyphDocument, PreviewKind.Binary),
        ["docx"] = new("Word document", GlyphDocument, PreviewKind.Binary),
        ["rtf"] = new("Rich text document", GlyphDocument, PreviewKind.Text),
        ["odt"] = new("OpenDocument text", GlyphDocument, PreviewKind.Binary),
        ["xls"] = new("Excel workbook", GlyphDocument, PreviewKind.Binary),
        ["xlsx"] = new("Excel workbook", GlyphDocument, PreviewKind.Binary),
        ["ods"] = new("OpenDocument sheet", GlyphDocument, PreviewKind.Binary),
        ["ppt"] = new("PowerPoint presentation", GlyphDocument, PreviewKind.Binary),
        ["pptx"] = new("PowerPoint presentation", GlyphDocument, PreviewKind.Binary),
        ["epub"] = new("E-book", GlyphDocument, PreviewKind.Binary),

        // Plain text and data
        ["txt"] = new("Text document", GlyphDocument, PreviewKind.Text),
        ["log"] = new("Log file", GlyphDocument, PreviewKind.Text),
        ["md"] = new("Markdown document", GlyphDocument, PreviewKind.Text),
        ["csv"] = new("Comma-separated values", GlyphDocument, PreviewKind.Text),
        ["tsv"] = new("Tab-separated values", GlyphDocument, PreviewKind.Text),
        ["ini"] = new("Configuration file", GlyphCode, PreviewKind.Text),
        ["cfg"] = new("Configuration file", GlyphCode, PreviewKind.Text),
        ["conf"] = new("Configuration file", GlyphCode, PreviewKind.Text),
        ["json"] = new("JSON file", GlyphCode, PreviewKind.Text),
        ["xml"] = new("XML file", GlyphCode, PreviewKind.Text),
        ["yaml"] = new("YAML file", GlyphCode, PreviewKind.Text),
        ["yml"] = new("YAML file", GlyphCode, PreviewKind.Text),
        ["toml"] = new("TOML file", GlyphCode, PreviewKind.Text),
        ["sql"] = new("SQL script", GlyphCode, PreviewKind.Text),

        // Code
        ["cs"] = new("C# source file", GlyphCode, PreviewKind.Text),
        ["csproj"] = new("C# project", GlyphCode, PreviewKind.Text),
        ["c"] = new("C source file", GlyphCode, PreviewKind.Text),
        ["h"] = new("C header file", GlyphCode, PreviewKind.Text),
        ["cpp"] = new("C++ source file", GlyphCode, PreviewKind.Text),
        ["hpp"] = new("C++ header file", GlyphCode, PreviewKind.Text),
        ["rs"] = new("Rust source file", GlyphCode, PreviewKind.Text),
        ["go"] = new("Go source file", GlyphCode, PreviewKind.Text),
        ["py"] = new("Python script", GlyphCode, PreviewKind.Text),
        ["js"] = new("JavaScript file", GlyphCode, PreviewKind.Text),
        ["ts"] = new("TypeScript file", GlyphCode, PreviewKind.Text),
        ["html"] = new("HTML document", GlyphCode, PreviewKind.Text),
        ["htm"] = new("HTML document", GlyphCode, PreviewKind.Text),
        ["css"] = new("Style sheet", GlyphCode, PreviewKind.Text),
        ["ps1"] = new("PowerShell script", GlyphCode, PreviewKind.Text),
        ["sh"] = new("Shell script", GlyphCode, PreviewKind.Text),
        ["bat"] = new("Batch file", GlyphCode, PreviewKind.Text),
        ["cmd"] = new("Batch file", GlyphCode, PreviewKind.Text),

        // Images
        ["png"] = new("PNG image", GlyphImage, PreviewKind.Image),
        ["jpg"] = new("JPEG image", GlyphImage, PreviewKind.Image),
        ["jpeg"] = new("JPEG image", GlyphImage, PreviewKind.Image),
        ["gif"] = new("GIF image", GlyphImage, PreviewKind.Image),
        ["bmp"] = new("Bitmap image", GlyphImage, PreviewKind.Image),
        ["tif"] = new("TIFF image", GlyphImage, PreviewKind.Image),
        ["tiff"] = new("TIFF image", GlyphImage, PreviewKind.Image),
        ["webp"] = new("WebP image", GlyphImage, PreviewKind.Image),
        ["ico"] = new("Icon", GlyphImage, PreviewKind.Image),
        ["heic"] = new("HEIC image", GlyphImage, PreviewKind.Image),
        ["svg"] = new("SVG image", GlyphImage, PreviewKind.Text),

        // Archives
        ["zip"] = new("Zip archive", GlyphArchive, PreviewKind.Binary),
        ["7z"] = new("7-Zip archive", GlyphArchive, PreviewKind.Binary),
        ["rar"] = new("RAR archive", GlyphArchive, PreviewKind.Binary),
        ["tar"] = new("Tar archive", GlyphArchive, PreviewKind.Binary),
        ["gz"] = new("Gzip archive", GlyphArchive, PreviewKind.Binary),
        ["bz2"] = new("Bzip2 archive", GlyphArchive, PreviewKind.Binary),
        ["xz"] = new("XZ archive", GlyphArchive, PreviewKind.Binary),
        ["iso"] = new("Disc image", GlyphArchive, PreviewKind.Binary),

        // Media
        ["mp4"] = new("MP4 video", GlyphVideo, PreviewKind.Binary),
        ["mkv"] = new("Matroska video", GlyphVideo, PreviewKind.Binary),
        ["mov"] = new("QuickTime video", GlyphVideo, PreviewKind.Binary),
        ["avi"] = new("AVI video", GlyphVideo, PreviewKind.Binary),
        ["webm"] = new("WebM video", GlyphVideo, PreviewKind.Binary),
        ["mp3"] = new("MP3 audio", GlyphAudio, PreviewKind.Binary),
        ["wav"] = new("WAV audio", GlyphAudio, PreviewKind.Binary),
        ["flac"] = new("FLAC audio", GlyphAudio, PreviewKind.Binary),
        ["m4a"] = new("MPEG-4 audio", GlyphAudio, PreviewKind.Binary),
        ["ogg"] = new("Ogg audio", GlyphAudio, PreviewKind.Binary),

        // Keys and secrets
        ["pem"] = new("PEM key or certificate", GlyphKeyFile, PreviewKind.Text),
        ["key"] = new("Private key", GlyphKeyFile, PreviewKind.Text),
        ["pub"] = new("Public key", GlyphKeyFile, PreviewKind.Text),
        ["asc"] = new("PGP armoured file", GlyphKeyFile, PreviewKind.Text),
        ["gpg"] = new("PGP file", GlyphKeyFile, PreviewKind.Binary),
        ["pfx"] = new("PKCS 12 store", GlyphKeyFile, PreviewKind.Binary),
        ["p12"] = new("PKCS 12 store", GlyphKeyFile, PreviewKind.Binary),
        ["cer"] = new("Certificate", GlyphKeyFile, PreviewKind.Binary),
        ["crt"] = new("Certificate", GlyphKeyFile, PreviewKind.Binary),
        ["kdbx"] = new("Password database", GlyphKeyFile, PreviewKind.Binary),
        ["dat"] = new("Data file", GlyphFile, PreviewKind.Binary),
        ["bastion"] = new("Bastion Vault", GlyphKeyFile, PreviewKind.Binary),
    };

    /// <summary>Names with no useful extension that are still recognisable, such as an SSH key.</summary>
    private static readonly Dictionary<string, FileTypeInfo> ByWholeName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["id_rsa"] = new("Private key", GlyphKeyFile, PreviewKind.Text),
        ["id_ed25519"] = new("Private key", GlyphKeyFile, PreviewKind.Text),
        ["ssh_ed25519"] = new("Private key", GlyphKeyFile, PreviewKind.Text),
        ["known_hosts"] = new("SSH known hosts", GlyphKeyFile, PreviewKind.Text),
        ["readme"] = new("Text document", GlyphDocument, PreviewKind.Text),
        ["licence"] = new("Text document", GlyphDocument, PreviewKind.Text),
        ["license"] = new("Text document", GlyphDocument, PreviewKind.Text),
        ["makefile"] = new("Makefile", GlyphCode, PreviewKind.Text),
        ["dockerfile"] = new("Dockerfile", GlyphCode, PreviewKind.Text),
    };

    /// <summary>What a folder is called and drawn with.</summary>
    public static FileTypeInfo Folder { get; } = new("Folder", GlyphFolder, PreviewKind.Binary);

    /// <summary>Describes an entry from its kind and name.</summary>
    /// <param name="kind">Folder or file.</param>
    /// <param name="name">Entry name, with or without an extension.</param>
    public static FileTypeInfo Describe(EntryKind kind, string? name) =>
        kind == EntryKind.Folder ? Folder : Describe(name);

    /// <summary>Describes a file from its name.</summary>
    /// <param name="name">File name, with or without an extension.</param>
    public static FileTypeInfo Describe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Unknown;
        }

        string extension = Extension(name);
        if (extension.Length > 0 && Table.TryGetValue(extension, out FileTypeInfo? known))
        {
            return known;
        }

        if (ByWholeName.TryGetValue(name, out FileTypeInfo? whole))
        {
            return whole;
        }

        return extension.Length == 0
            ? Unknown
            : new FileTypeInfo(extension.ToUpperInvariant() + " file", GlyphFile, PreviewKind.Binary);
    }

    /// <summary>The extension of a name without its dot; empty when the name has none.</summary>
    /// <param name="name">Entry name.</param>
    public static string Extension(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..];
    }

    /// <summary>
    /// How many characters F2 preselects: everything before the last dot, or the whole name when
    /// there is no extension or the name is a dotfile.
    /// </summary>
    /// <param name="name">Entry name.</param>
    public static int StemLength(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        int dot = name.LastIndexOf('.');
        return dot <= 0 ? name.Length : dot;
    }
}
