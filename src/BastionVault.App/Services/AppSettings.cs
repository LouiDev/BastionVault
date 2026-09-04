using BastionVault.Core;
using System.IO;

namespace BastionVault.App.Services;

/// <summary>Which theme the app renders in.</summary>
public enum AppTheme
{
    /// <summary>Lamplight, always dark.</summary>
    Dark,

    /// <summary>Lamplight, replaced by the SystemColors mapping while Windows is in high contrast.</summary>
    HighContrastAuto,
}

/// <summary>Row height of the entry list.</summary>
public enum RowDensity
{
    /// <summary>24 px rows.</summary>
    Compact,

    /// <summary>28 px rows (the default).</summary>
    Comfortable,

    /// <summary>32 px rows.</summary>
    Spacious,
}

/// <summary>Where a session puts its staging container.</summary>
public enum StagingLocation
{
    /// <summary>Next to the vault file, so staging and vault share a volume.</summary>
    BesideVault,

    /// <summary>The user's temp directory.</summary>
    SystemTemp,

    /// <summary>A directory the user picked (<see cref="AppSettings.StagingCustomPath"/>).</summary>
    Custom,
}

/// <summary>Persisted width, order and visibility of one entry-list column.</summary>
public sealed class ColumnState
{
    /// <summary>Stable key of the column ("name", "size", "modified", ...).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Width in device-independent pixels.</summary>
    public double Width { get; set; } = 160;

    /// <summary>Position of the column, left to right, starting at zero.</summary>
    public int Order { get; set; }

    /// <summary>False when the user hid the column.</summary>
    public bool IsVisible { get; set; } = true;
}

/// <summary>Persisted column widths, order and sort of the entry list.</summary>
public sealed class ColumnLayout
{
    /// <summary>State of each known column.</summary>
    public List<ColumnState> Columns { get; set; } = [];

    /// <summary>Key of the column the list is sorted by.</summary>
    public string SortColumn { get; set; } = "name";

    /// <summary>True when the sort is ascending.</summary>
    public bool SortAscending { get; set; } = true;
}

/// <summary>Persisted window position; validated against the current monitors before it is applied.</summary>
public sealed class WindowPlacement
{
    /// <summary>Left edge in virtual-screen coordinates.</summary>
    public double Left { get; set; }

    /// <summary>Top edge in virtual-screen coordinates.</summary>
    public double Top { get; set; }

    /// <summary>Window width.</summary>
    public double Width { get; set; } = 1180;

    /// <summary>Window height.</summary>
    public double Height { get; set; } = 760;

    /// <summary>True when the window was maximised when it was last closed.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>False until a real placement has been recorded.</summary>
    public bool HasValue { get; set; }
}

/// <summary>
/// Everything the app remembers between runs (UI-CONTRACT.md section 5). Stored as JSON under
/// <c>%LOCALAPPDATA%\BastionVault\settings.json</c>; no vault content and no credentials are ever
/// written here.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Schema version of the settings file; bumped when a field changes meaning.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Theme selection.</summary>
    public AppTheme Theme { get; set; } = AppTheme.HighContrastAuto;

    /// <summary>Idle minutes before the vault locks itself; 0 turns auto-lock off.</summary>
    public int AutoLockMinutes { get; set; } = 10;

    /// <summary>Preset offered first in the New vault and Change credentials dialogs.</summary>
    public KdfPreset DefaultKdfPreset { get; set; } = KdfPreset.Standard;

    /// <summary>Row height of the entry list.</summary>
    public RowDensity RowDensity { get; set; } = RowDensity.Comfortable;

    /// <summary>Widths, order and sort of the entry-list columns.</summary>
    public ColumnLayout ColumnLayout { get; set; } = new();

    /// <summary>Where the window was when it was last closed.</summary>
    public WindowPlacement WindowPlacement { get; set; } = new();

    /// <summary>False to stop recording opened vaults at all (and to drop the existing list).</summary>
    public bool RememberRecentVaults { get; set; } = true;

    /// <summary>
    /// True to remember the keyfile path per vault. Off by default: a remembered keyfile path is
    /// not a second factor, only a longer password (THREAT-MODEL.md A3).
    /// </summary>
    public bool RememberKeyFilePaths { get; set; }

    /// <summary>Where a session puts its staging container.</summary>
    public StagingLocation StagingLocation { get; set; } = StagingLocation.BesideVault;

    /// <summary>Directory used when <see cref="StagingLocation"/> is <see cref="StagingLocation.Custom"/>.</summary>
    public string StagingCustomPath { get; set; } = string.Empty;

    /// <summary>Keeps the window out of screen captures and Recall (<c>WDA_EXCLUDEFROMCAPTURE</c>).</summary>
    public bool ExcludeFromScreenCapture { get; set; } = true;

    /// <summary>Shows the preview pane.</summary>
    public bool PreviewEnabled { get; set; } = true;

    /// <summary>Masks entry names while the window is not active.</summary>
    public bool MaskNamesWhenInactive { get; set; }

    /// <summary>Blurs the preview while the window is not active.</summary>
    public bool BlurPreviewWhenInactive { get; set; } = true;

    /// <summary>Pads the data section on save so the file size does not reveal the plaintext volume.</summary>
    public bool SizeObfuscation { get; set; }

    /// <summary>Rewrites every blob under a fresh key on save, so versions cannot be diffed.</summary>
    public bool ReencryptOnSave { get; set; }

    /// <summary>True until the first-run screen has been shown once.</summary>
    public bool ShowFirstRun { get; set; } = true;

    /// <summary>Returns a deep copy, used to diff a Settings dialog against the live settings.</summary>
    public AppSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Theme = Theme,
        AutoLockMinutes = AutoLockMinutes,
        DefaultKdfPreset = DefaultKdfPreset,
        RowDensity = RowDensity,
        ColumnLayout = new ColumnLayout
        {
            SortColumn = ColumnLayout.SortColumn,
            SortAscending = ColumnLayout.SortAscending,
            Columns = [.. ColumnLayout.Columns.Select(c => new ColumnState
            {
                Key = c.Key,
                Width = c.Width,
                Order = c.Order,
                IsVisible = c.IsVisible,
            })],
        },
        WindowPlacement = new WindowPlacement
        {
            Left = WindowPlacement.Left,
            Top = WindowPlacement.Top,
            Width = WindowPlacement.Width,
            Height = WindowPlacement.Height,
            IsMaximized = WindowPlacement.IsMaximized,
            HasValue = WindowPlacement.HasValue,
        },
        RememberRecentVaults = RememberRecentVaults,
        RememberKeyFilePaths = RememberKeyFilePaths,
        StagingLocation = StagingLocation,
        StagingCustomPath = StagingCustomPath,
        ExcludeFromScreenCapture = ExcludeFromScreenCapture,
        PreviewEnabled = PreviewEnabled,
        MaskNamesWhenInactive = MaskNamesWhenInactive,
        BlurPreviewWhenInactive = BlurPreviewWhenInactive,
        SizeObfuscation = SizeObfuscation,
        ReencryptOnSave = ReencryptOnSave,
        ShowFirstRun = ShowFirstRun,
    };

    /// <summary>Copies every value of <paramref name="other"/> into this instance.</summary>
    /// <param name="other">Source of the values.</param>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);

        SchemaVersion = other.SchemaVersion;
        Theme = other.Theme;
        AutoLockMinutes = other.AutoLockMinutes;
        DefaultKdfPreset = other.DefaultKdfPreset;
        RowDensity = other.RowDensity;
        ColumnLayout = other.ColumnLayout;
        WindowPlacement = other.WindowPlacement;
        RememberRecentVaults = other.RememberRecentVaults;
        RememberKeyFilePaths = other.RememberKeyFilePaths;
        StagingLocation = other.StagingLocation;
        StagingCustomPath = other.StagingCustomPath;
        ExcludeFromScreenCapture = other.ExcludeFromScreenCapture;
        PreviewEnabled = other.PreviewEnabled;
        MaskNamesWhenInactive = other.MaskNamesWhenInactive;
        BlurPreviewWhenInactive = other.BlurPreviewWhenInactive;
        SizeObfuscation = other.SizeObfuscation;
        ReencryptOnSave = other.ReencryptOnSave;
        ShowFirstRun = other.ShowFirstRun;
    }
}

/// <summary>One entry of the recent-vault list (stored DPAPI-protected, current user).</summary>
/// <param name="Path">Full path of the vault file.</param>
/// <param name="LastOpenedUtc">When the vault was last opened.</param>
/// <param name="KeyFilePath">Remembered keyfile path, only when the user opted in.</param>
public sealed record RecentVault(string Path, DateTimeOffset LastOpenedUtc, string? KeyFilePath = null)
{
    /// <summary>File name without the directory, for display.</summary>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Directory the vault lives in, for the second line of a recents row.</summary>
    public string DisplayDirectory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>What the internal clipboard currently holds.</summary>
/// <param name="Ids">The entries that were cut or copied.</param>
/// <param name="IsCut">True for a cut, false for a copy.</param>
/// <param name="SourceVaultPath">Vault the entries came from; a paste into another vault is refused.</param>
public sealed record ClipboardOp(IReadOnlyList<EntryId> Ids, bool IsCut, string SourceVaultPath);
