using Bastion.App.Services;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>What the Change credentials dialog produces.</summary>
/// <param name="CurrentPassword">The password the user typed to confirm they are the owner; the caller disposes it.</param>
/// <param name="NewPassword">The new password; the caller disposes it.</param>
/// <param name="KeyFile">The new keyfile, or <see langword="null"/> for none; the caller disposes it.</param>
/// <param name="Kdf">Argon2id parameters to store.</param>
/// <param name="Mode">Full re-key, or rewrap only.</param>
public sealed record ChangeCredentialsResult(
    Passphrase? CurrentPassword,
    Passphrase? NewPassword,
    KeyFile? KeyFile,
    KdfParameters Kdf,
    CredentialChangeMode Mode);

/// <summary>
/// The Change credentials dialog (UI-CONTRACT.md section 7). The mode choice is stated honestly:
/// a full re-key rewrites every blob under a fresh key, while "rewrap only" is instant but leaves
/// every existing byte encrypted under the same vault key - anyone who already copied the file
/// and knows the old password can still read the old copy.
/// </summary>
public sealed partial class ChangeCredentialsDialogViewModel : DialogViewModelBase<ChangeCredentialsResult>
{
    private readonly IFileDialogService _files;
    private readonly IKdfEstimator _estimator;
    private readonly ILog _log;
    private readonly long _plaintextBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasCurrentPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(StrengthSentence))]
    [NotifyPropertyChangedFor(nameof(StrengthLevelText))]
    private PasswordStrengthResult _strength = PasswordStrengthResult.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _passwordsMatch;

    [ObservableProperty]
    private bool _isCapsLockOn;

    [ObservableProperty]
    private string? _keyFilePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthSentence))]
    [NotifyPropertyChangedFor(nameof(CostLine))]
    private KdfPresetOption _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CostLine))]
    [NotifyPropertyChangedFor(nameof(ModeCaveat))]
    private CredentialChangeMode _mode = CredentialChangeMode.Rekey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    /// <summary>Creates the dialog.</summary>
    /// <param name="files">File pickers for the keyfile.</param>
    /// <param name="estimator">Measures the cost of each preset on this machine.</param>
    /// <param name="currentKdf">The parameters currently stored in the header.</param>
    /// <param name="plaintextBytes">Total plaintext volume, used for the cost line.</param>
    /// <param name="log">Log.</param>
    public ChangeCredentialsDialogViewModel(
        IFileDialogService files, IKdfEstimator estimator, KdfParameters currentKdf, long plaintextBytes, ILog log)
    {
        ArgumentNullException.ThrowIfNull(currentKdf);

        _files = files;
        _estimator = estimator;
        _plaintextBytes = plaintextBytes;
        _log = log;

        Title = "Change password";
        Presets =
        [
            new KdfPresetOption(KdfPreset.Fast, KdfParameters.FromPreset(KdfPreset.Fast),
                "Quick to open. Choose it only with a long, random password."),
            new KdfPresetOption(KdfPreset.Standard, KdfParameters.FromPreset(KdfPreset.Standard),
                "The default. Half a gigabyte of memory per guess."),
            new KdfPresetOption(KdfPreset.Strong, KdfParameters.FromPreset(KdfPreset.Strong),
                "A gigabyte and four passes. Noticeably slower to open."),
        ];

        _selectedPreset = Presets.FirstOrDefault(p => p.Parameters == currentKdf) ?? Presets[1];
    }

    /// <summary>The three presets.</summary>
    public IReadOnlyList<KdfPresetOption> Presets { get; }

    /// <summary>True when a keyfile is attached.</summary>
    public bool HasKeyFile => !string.IsNullOrWhiteSpace(KeyFilePath);

    /// <summary>True when there is an error to show inline.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The strength band as a word.</summary>
    public string StrengthLevelText => Strength.Length == 0 ? string.Empty : Strength.Level switch
    {
        PasswordStrengthLevel.VeryWeak => "Very weak",
        PasswordStrengthLevel.Weak => "Weak",
        PasswordStrengthLevel.Fair => "Fair",
        PasswordStrengthLevel.Strong => "Strong",
        _ => "Very strong",
    };

    /// <summary>The crack-time sentence at the selected preset.</summary>
    public string StrengthSentence =>
        PasswordStrength.Sentence(Strength, SelectedPreset.Parameters, SelectedPreset.Name);

    /// <summary>The honest caveat attached to the selected mode.</summary>
    public string ModeCaveat => Mode == CredentialChangeMode.Rekey
        ? "Every blob is re-encrypted under a fresh key at the next save. Old copies of the file stay readable with the old password, but nothing new leaks from them."
        : "Only the header is rewritten. This is instant, but every byte on disk stays encrypted under the same vault key — a copy someone already took stays readable with the old password.";

    /// <summary>What the change will cost at the next save.</summary>
    public string CostLine
    {
        get
        {
            if (Mode == CredentialChangeMode.RewrapOnly)
            {
                return "Applied at the next save; the save rewrites only the header and the index.";
            }

            double gigabytes = _plaintextBytes / (1024.0 * 1024 * 1024);
            double minutes = Math.Max(0.1, _plaintextBytes / (120.0 * 1024 * 1024) / 60);
            return $"Applied at the next save; that save will rewrite about {gigabytes:N2} GB, roughly {minutes:N0} minute{(minutes >= 1.5 ? "s" : string.Empty)}.";
        }
    }

    /// <summary>True when the dialog can be applied.</summary>
    public bool CanApply =>
        HasCurrentPassword
        && PasswordsMatch
        && Strength.Length >= PasswordStrength.MinimumLength;

    /// <summary>Measures every preset on this machine.</summary>
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
        }
    }

    /// <summary>Records a fresh measurement of the new-password fields taken by the view.</summary>
    /// <param name="strength">Result of measuring the new password.</param>
    /// <param name="matches">True when the confirmation field holds the same characters.</param>
    /// <param name="hasCurrent">True when the current-password field is not empty.</param>
    public void ApplyPassword(PasswordStrengthResult strength, bool matches, bool hasCurrent)
    {
        Strength = strength;
        PasswordsMatch = matches;
        HasCurrentPassword = hasCurrent;
    }

    /// <summary>Closes the dialog with the collected result.</summary>
    /// <param name="currentPassword">The current password, built by the view.</param>
    /// <param name="newPassword">The new password, built by the view.</param>
    public void Submit(Passphrase? currentPassword, Passphrase? newPassword)
    {
        if (!CanApply)
        {
            currentPassword?.Dispose();
            newPassword?.Dispose();
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
                Error = "That keyfile could not be read.";
                currentPassword?.Dispose();
                newPassword?.Dispose();
                return;
            }
        }

        Close(new ChangeCredentialsResult(currentPassword, newPassword, keyFile, SelectedPreset.Parameters, Mode));
    }

    /// <summary>Attaches an existing keyfile.</summary>
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

    /// <summary>Removes the keyfile from the new credentials.</summary>
    [RelayCommand]
    public void RemoveKeyFile()
    {
        KeyFilePath = null;
        OnPropertyChanged(nameof(HasKeyFile));
    }
}
