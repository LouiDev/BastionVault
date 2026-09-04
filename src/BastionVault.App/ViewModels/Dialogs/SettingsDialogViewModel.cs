using BastionVault.App.Services;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels.Dialogs;

/// <summary>One entry of a settings drop-down: the value plus the words the user reads.</summary>
/// <typeparam name="T">Type of the underlying value.</typeparam>
/// <param name="Value">The value stored in <see cref="AppSettings"/>.</param>
/// <param name="Label">What the drop-down shows.</param>
public sealed record Choice<T>(T Value, string Label);

/// <summary>
/// The Settings dialog. It edits a copy of <see cref="AppSettings"/> and only writes it back when
/// the user presses Save, so Escape really is a cancel. Registering the <c>.bastion</c> file type
/// happens here and nowhere else (UI-CONTRACT.md section 7).
/// </summary>
public sealed partial class SettingsDialogViewModel : DialogViewModelBase<AppSettings>
{
    private readonly ISettingsService _settings;
    private readonly IShellIntegration _shell;
    private readonly IRecentVaults _recent;
    private readonly IFileDialogService _files;
    private readonly ILog _log;

    [ObservableProperty]
    private int _autoLockMinutes;

    [ObservableProperty]
    private KdfPreset _defaultKdfPreset;

    [ObservableProperty]
    private RowDensity _rowDensity;

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private bool _rememberRecentVaults;

    [ObservableProperty]
    private bool _rememberKeyFilePaths;

    [ObservableProperty]
    private StagingLocation _stagingLocation;

    [ObservableProperty]
    private string _stagingCustomPath = string.Empty;

    [ObservableProperty]
    private bool _excludeFromScreenCapture;

    [ObservableProperty]
    private bool _previewEnabled;

    [ObservableProperty]
    private bool _maskNamesWhenInactive;

    [ObservableProperty]
    private bool _blurPreviewWhenInactive;

    [ObservableProperty]
    private bool _sizeObfuscation;

    [ObservableProperty]
    private bool _reencryptOnSave;

    [ObservableProperty]
    private bool _showFirstRun;

    [ObservableProperty]
    private bool _isFileTypeRegistered;

    [ObservableProperty]
    private string? _status;

    /// <summary>Creates the dialog over the live settings.</summary>
    /// <param name="settings">The settings service.</param>
    /// <param name="shell">Shell integration, for the file association.</param>
    /// <param name="recent">Recent vaults, for "Clear recent vaults".</param>
    /// <param name="files">File pickers, for the custom staging directory.</param>
    /// <param name="log">Log.</param>
    public SettingsDialogViewModel(
        ISettingsService settings, IShellIntegration shell, IRecentVaults recent, IFileDialogService files, ILog log)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _shell = shell;
        _recent = recent;
        _files = files;
        _log = log;

        Title = "Settings";

        AppSettings current = settings.Current;
        _autoLockMinutes = current.AutoLockMinutes;
        _defaultKdfPreset = current.DefaultKdfPreset;
        _rowDensity = current.RowDensity;
        _theme = current.Theme;
        _rememberRecentVaults = current.RememberRecentVaults;
        _rememberKeyFilePaths = current.RememberKeyFilePaths;
        _stagingLocation = current.StagingLocation;
        _stagingCustomPath = current.StagingCustomPath;
        _excludeFromScreenCapture = current.ExcludeFromScreenCapture;
        _previewEnabled = current.PreviewEnabled;
        _maskNamesWhenInactive = current.MaskNamesWhenInactive;
        _blurPreviewWhenInactive = current.BlurPreviewWhenInactive;
        _sizeObfuscation = current.SizeObfuscation;
        _reencryptOnSave = current.ReencryptOnSave;
        _showFirstRun = current.ShowFirstRun;
        _isFileTypeRegistered = shell.IsRegistered;
    }

    /// <summary>Auto-lock values offered in the drop-down, in minutes; 0 is "never".</summary>
    public IReadOnlyList<Choice<int>> AutoLockChoices { get; } =
    [
        new(0, "Never"),
        new(1, "1 minute"),
        new(2, "2 minutes"),
        new(5, "5 minutes"),
        new(10, "10 minutes"),
        new(15, "15 minutes"),
        new(30, "30 minutes"),
        new(60, "1 hour"),
    ];

    /// <summary>The three KDF presets.</summary>
    public IReadOnlyList<Choice<KdfPreset>> KdfPresets { get; } =
    [
        new(KdfPreset.Fast, "Fast — 64 MiB, 3 passes"),
        new(KdfPreset.Standard, "Standard — 512 MiB, 3 passes"),
        new(KdfPreset.Strong, "Strong — 1 GiB, 4 passes"),
    ];

    /// <summary>The three row densities.</summary>
    public IReadOnlyList<Choice<RowDensity>> RowDensities { get; } =
    [
        new(RowDensity.Compact, "Compact — 24 px rows"),
        new(RowDensity.Comfortable, "Comfortable — 28 px rows"),
        new(RowDensity.Spacious, "Spacious — 32 px rows"),
    ];

    /// <summary>
    /// The two theme choices. The second label is a whole phrase: it used to read "Lamplight,
    /// high contrast when Windows is", which stops mid-clause, and the drop-down showed the
    /// dangling fragment with no way to tell what the option did.
    /// </summary>
    public IReadOnlyList<Choice<AppTheme>> Themes { get; } =
    [
        new(AppTheme.Dark, "Lamplight (dark)"),
        new(AppTheme.HighContrastAuto, "Lamplight, high contrast with Windows"),
    ];

    /// <summary>The three staging locations.</summary>
    public IReadOnlyList<Choice<StagingLocation>> StagingLocations { get; } =
    [
        new(StagingLocation.BesideVault, "Next to the vault file"),
        new(StagingLocation.SystemTemp, "The system temp directory"),
        new(StagingLocation.Custom, "A directory I choose"),
    ];

    /// <summary>True when the custom staging path field is relevant.</summary>
    public bool IsCustomStaging => StagingLocation == StagingLocation.Custom;

    /// <summary>Writes every field back and closes.</summary>
    [RelayCommand]
    public void Save()
    {
        AppSettings edited = _settings.Current.Clone();
        edited.AutoLockMinutes = AutoLockMinutes;
        edited.DefaultKdfPreset = DefaultKdfPreset;
        edited.RowDensity = RowDensity;
        edited.Theme = Theme;
        edited.RememberRecentVaults = RememberRecentVaults;
        edited.RememberKeyFilePaths = RememberKeyFilePaths;
        edited.StagingLocation = StagingLocation;
        edited.StagingCustomPath = StagingCustomPath;
        edited.ExcludeFromScreenCapture = ExcludeFromScreenCapture;
        edited.PreviewEnabled = PreviewEnabled;
        edited.MaskNamesWhenInactive = MaskNamesWhenInactive;
        edited.BlurPreviewWhenInactive = BlurPreviewWhenInactive;
        edited.SizeObfuscation = SizeObfuscation;
        edited.ReencryptOnSave = ReencryptOnSave;
        edited.ShowFirstRun = ShowFirstRun;

        Close(edited);
    }

    /// <summary>Registers or unregisters the <c>.bastion</c> file type for the current user.</summary>
    [RelayCommand]
    public void ToggleFileAssociation()
    {
        try
        {
            if (IsFileTypeRegistered)
            {
                _shell.UnregisterFileAssociation();
                IsFileTypeRegistered = false;
                Status = "The .bastion file type is no longer registered.";
            }
            else
            {
                _shell.RegisterFileAssociation();
                IsFileTypeRegistered = true;
                Status = "Double-clicking a .bastion file now opens Bastion Vault.";
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The file association could not be changed.", ex);
            Status = "Windows refused the change. Bastion Vault only ever writes this under your own user.";
        }
    }

    /// <summary>Empties the recent-vault list.</summary>
    [RelayCommand]
    public void ClearRecentVaults()
    {
        _recent.Clear();
        Status = "The recent-vault list is empty.";
    }

    /// <summary>Picks the custom staging directory.</summary>
    [RelayCommand]
    public void ChooseStagingPath()
    {
        string? picked = _files.PickExportFolder();
        if (picked is not null)
        {
            StagingCustomPath = picked;
        }
    }

    partial void OnStagingLocationChanged(StagingLocation value) => OnPropertyChanged(nameof(IsCustomStaging));
}
