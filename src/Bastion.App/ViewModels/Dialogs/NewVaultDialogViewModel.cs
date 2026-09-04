using Bastion.App.Services;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>What the New vault dialog produces.</summary>
/// <param name="Path">Full path of the vault file to create.</param>
/// <param name="Password">The password; the caller disposes it. Null only in the demo host, which holds no key material.</param>
/// <param name="KeyFile">The optional keyfile; the caller disposes it.</param>
/// <param name="Kdf">Argon2id parameters to store in the header.</param>
public sealed record NewVaultResult(string Path, Passphrase? Password, KeyFile? KeyFile, KdfParameters Kdf);

/// <summary>One KDF preset as the radio group shows it, with a measured estimate for this machine.</summary>
public sealed partial class KdfPresetOption : ObservableObject
{
    [ObservableProperty]
    private string _estimate = "measuring...";

    /// <summary>Creates an option.</summary>
    /// <param name="preset">Which preset this is.</param>
    /// <param name="parameters">The parameters the preset stands for.</param>
    /// <param name="description">One line saying what the trade-off is.</param>
    public KdfPresetOption(KdfPreset preset, KdfParameters parameters, string description)
    {
        Preset = preset;
        Parameters = parameters;
        Description = description;
    }

    /// <summary>Which preset this is.</summary>
    public KdfPreset Preset { get; }

    /// <summary>The parameters the preset stands for.</summary>
    public KdfParameters Parameters { get; }

    /// <summary>Display name ("Fast", "Standard", "Strong").</summary>
    public string Name => Preset.ToString();

    /// <summary>One line saying what the trade-off is.</summary>
    public string Description { get; }

    /// <summary>The parameters in instrument type: "512 MiB - 3 passes - 4 lanes".</summary>
    public string ParameterText =>
        $"{Parameters.MemoryKiB / 1024} MiB · {Parameters.Iterations} passes · {Parameters.Parallelism} lanes";
}

/// <summary>
/// The New vault dialog (UI-CONTRACT.md section 7). Everything about the password is handled in
/// the view's code-behind and arrives here as a measurement, never as text: the view model knows
/// the strength, the length and whether the two fields match, and nothing more.
/// </summary>
public sealed partial class NewVaultDialogViewModel : DialogViewModelBase<NewVaultResult>
{
    private readonly IFileDialogService _files;
    private readonly IKdfEstimator _estimator;
    private readonly ILog _log;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasPath))]
    private string _path = string.Empty;

    [ObservableProperty]
    private string? _keyFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    [NotifyPropertyChangedFor(nameof(StrengthSentence))]
    private KdfPresetOption _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    private bool _acknowledged;

    [ObservableProperty]
    private bool _isCapsLockOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    [NotifyPropertyChangedFor(nameof(StrengthSentence))]
    [NotifyPropertyChangedFor(nameof(StrengthLevelText))]
    [NotifyPropertyChangedFor(nameof(StrengthFraction))]
    [NotifyPropertyChangedFor(nameof(Weakness))]
    [NotifyPropertyChangedFor(nameof(IsTooShort))]
    private PasswordStrengthResult _strength = PasswordStrengthResult.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyPropertyChangedFor(nameof(BlockingReason))]
    [NotifyPropertyChangedFor(nameof(HasBlockingReason))]
    private bool _passwordsMatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>Creates the dialog.</summary>
    /// <param name="files">File pickers for the vault path and the keyfile.</param>
    /// <param name="estimator">Measures the cost of each preset on this machine.</param>
    /// <param name="defaultPreset">Preset selected when the dialog opens.</param>
    /// <param name="log">Log.</param>
    public NewVaultDialogViewModel(
        IFileDialogService files, IKdfEstimator estimator, KdfPreset defaultPreset, ILog log)
    {
        _files = files;
        _estimator = estimator;
        _log = log;

        Title = "New vault";
        Presets =
        [
            new KdfPresetOption(KdfPreset.Fast, KdfParameters.FromPreset(KdfPreset.Fast),
                "Quick to open. Choose it only with a long, random password."),
            new KdfPresetOption(KdfPreset.Standard, KdfParameters.FromPreset(KdfPreset.Standard),
                "The default. Half a gigabyte of memory per guess."),
            new KdfPresetOption(KdfPreset.Strong, KdfParameters.FromPreset(KdfPreset.Strong),
                "A gigabyte and four passes. Noticeably slower to open."),
        ];

        _selectedPreset = Presets.FirstOrDefault(p => p.Preset == defaultPreset) ?? Presets[1];
    }

    /// <summary>The three presets, in the order the radio group shows them.</summary>
    public IReadOnlyList<KdfPresetOption> Presets { get; }

    /// <summary>True once a vault path has been chosen.</summary>
    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    /// <summary>True when a keyfile has been chosen.</summary>
    public bool HasKeyFile => !string.IsNullOrWhiteSpace(KeyFilePath);

    /// <summary>True when there is an error to show inline.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True when the password is shorter than the hard minimum.</summary>
    public bool IsTooShort => Strength.Length is > 0 and < PasswordStrength.MinimumLength;

    /// <summary>The strength band as a word.</summary>
    public string StrengthLevelText => Strength.Length == 0 ? string.Empty : Strength.Level switch
    {
        PasswordStrengthLevel.VeryWeak => "Very weak",
        PasswordStrengthLevel.Weak => "Weak",
        PasswordStrengthLevel.Fair => "Fair",
        PasswordStrengthLevel.Strong => "Strong",
        _ => "Very strong",
    };

    /// <summary>The strength band as a 0..1 fraction, for the meter.</summary>
    public double StrengthFraction => Strength.Length == 0 ? 0 : Strength.Level switch
    {
        PasswordStrengthLevel.VeryWeak => 0.15,
        PasswordStrengthLevel.Weak => 0.35,
        PasswordStrengthLevel.Fair => 0.6,
        PasswordStrengthLevel.Strong => 0.85,
        _ => 1.0,
    };

    /// <summary>The one useful thing to say about the password's structure, if anything.</summary>
    public string? Weakness => Strength.Weakness;

    /// <summary>The crack-time sentence at the selected preset.</summary>
    public string StrengthSentence =>
        PasswordStrength.Sentence(Strength, SelectedPreset.Parameters, SelectedPreset.Name);

    /// <summary>True when every requirement is met and the vault can be created.</summary>
    public bool CanCreate =>
        HasPath
        && Acknowledged
        && PasswordsMatch
        && Strength.Length >= PasswordStrength.MinimumLength;

    /// <summary>
    /// Why the primary action is still disabled, or <see langword="null"/> when it is not. A
    /// disabled primary with no stated reason reads as a broken dialog rather than a gated one.
    /// </summary>
    public string? BlockingReason
    {
        get
        {
            if (!HasPath)
            {
                return "Choose where the vault file goes to continue.";
            }

            if (Strength.Length < PasswordStrength.MinimumLength)
            {
                return $"The password needs at least {PasswordStrength.MinimumLength} characters.";
            }

            if (!PasswordsMatch)
            {
                return "The two passwords do not match yet.";
            }

            return Acknowledged ? null : "Tick the acknowledgement to continue.";
        }
    }

    /// <summary>True while <see cref="BlockingReason"/> has something to say.</summary>
    public bool HasBlockingReason => BlockingReason is not null;

    /// <summary>
    /// Measures every preset on this machine and fills in the estimates. Nobody awaits this task
    /// until the dialog closes, so it must not be able to fault: an estimate is a number in a
    /// dialog, and a benchmark that cannot run says so in the line it was going to fill.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task MeasurePresetsAsync(CancellationToken ct)
    {
        foreach (KdfPresetOption option in Presets)
        {
            try
            {
                TimeSpan estimate = await _estimator.EstimateAsync(option.Parameters, ct).ConfigureAwait(true);
                option.Estimate = estimate.TotalSeconds < 1
                    ? $"about {estimate.TotalMilliseconds:N0} ms to open"
                    : $"about {estimate.TotalSeconds:N1} s to open";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Warn("A key-derivation preset could not be measured.", ex);
                option.Estimate = "not measured";
            }
        }
    }

    /// <summary>Records a fresh strength measurement taken by the view.</summary>
    /// <param name="strength">Result of measuring the password field.</param>
    /// <param name="matches">True when the confirmation field holds the same characters.</param>
    public void ApplyPassword(PasswordStrengthResult strength, bool matches)
    {
        Strength = strength;
        PasswordsMatch = matches;
    }

    /// <summary>Closes the dialog with the collected result.</summary>
    /// <param name="password">The password, built by the view from its password box.</param>
    public void Submit(Passphrase? password)
    {
        if (!CanCreate)
        {
            password?.Dispose();
            return;
        }

        KeyFile? keyFile = null;
        if (HasKeyFile)
        {
            try
            {
                keyFile = KeyFile.Load(KeyFilePath!);
            }
            catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
            {
                _log.Warn("The chosen keyfile could not be read.", ex);
                Error = "That keyfile could not be read. Choose another file, or create the vault without one.";
                password?.Dispose();
                return;
            }
        }

        Close(new NewVaultResult(Path, password, keyFile, SelectedPreset.Parameters));
    }

    /// <summary>Picks where the vault file goes.</summary>
    [RelayCommand]
    public void ChoosePath()
    {
        string? picked = _files.PickVaultToCreate("Vault");
        if (picked is not null)
        {
            Path = picked;
            Error = null;
        }
    }

    /// <summary>Picks an existing keyfile.</summary>
    [RelayCommand]
    public void ChooseKeyFile()
    {
        string? picked = _files.PickKeyFile();
        if (picked is not null)
        {
            KeyFilePath = picked;
            OnPropertyChanged(nameof(HasKeyFile));
        }
    }

    /// <summary>Generates a new random keyfile at a chosen path.</summary>
    [RelayCommand]
    public void GenerateKeyFile()
    {
        string? picked = _files.PickKeyFileToCreate();
        if (picked is null)
        {
            return;
        }

        try
        {
            byte[] content = KeyFile.GenerateContent();
            File.WriteAllBytes(picked, content);
            Array.Clear(content);
            KeyFilePath = picked;
            Error = null;
            OnPropertyChanged(nameof(HasKeyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotImplementedException)
        {
            _log.Warn("A keyfile could not be generated.", ex);
            Error = "That keyfile could not be written. Choose another location.";
        }
    }

    /// <summary>Forgets the chosen keyfile.</summary>
    [RelayCommand]
    public void RemoveKeyFile()
    {
        KeyFilePath = null;
        OnPropertyChanged(nameof(HasKeyFile));
    }
}
