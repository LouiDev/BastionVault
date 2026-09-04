using System.Globalization;
using Bastion.App.Services;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels;

/// <summary>How an unlock attempt ended.</summary>
public enum UnlockOutcome
{
    /// <summary>The vault is open.</summary>
    Success,

    /// <summary>Wrong password, wrong or missing keyfile, or an altered header. One bucket by design (FORMAT.md section 9).</summary>
    WrongCredentials,

    /// <summary>The credentials were right but the vault is damaged; saving over it would make things worse.</summary>
    Damaged,

    /// <summary>The file is not a vault, or its header is unsupported.</summary>
    NotAVault,

    /// <summary>The file could not be read at all.</summary>
    Unreadable,

    /// <summary>The attempt was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// The unlock card (UI-CONTRACT.md section 7). It states what it is about to spend before it
/// spends it, distinguishes the three failure classes of FORMAT.md section 9, selects the password
/// instead of clearing it after a bad attempt, and slows down after three failures.
/// </summary>
public sealed partial class UnlockViewModel : ObservableObject
{
    private static readonly TimeSpan SoftDelay = TimeSpan.FromSeconds(1);
    private const int SoftDelayAfterFailures = 3;

    private readonly ILog _log;

    [ObservableProperty]
    private string _vaultPath = string.Empty;

    [ObservableProperty]
    private string _vaultName = string.Empty;

    [ObservableProperty]
    private string _headerLine = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyFile))]
    private string? _keyFilePath;

    [ObservableProperty]
    private bool _rememberKeyFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isDeriving;

    [ObservableProperty]
    private string _derivingLabel = "Unlock";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    private string? _statusLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRollbackWarning))]
    private string? _rollbackWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _hasPassword;

    [ObservableProperty]
    private bool _isCapsLockOn;

    /// <summary>Creates the card.</summary>
    /// <param name="files">File picker for the keyfile.</param>
    /// <param name="log">Log.</param>
    public UnlockViewModel(IFileDialogService files, ILog log)
    {
        Files = files;
        _log = log;
    }

    /// <summary>Raised after a failed attempt so the view can select the password instead of clearing it.</summary>
    public event EventHandler? SelectPasswordRequested;

    /// <summary>Raised when the card wants the password field to take focus.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Performs the actual unlock; the shell supplies it because only the shell knows whether this is an open or a re-unlock.</summary>
    public Func<Passphrase?, KeyFile?, CancellationToken, Task<UnlockOutcome>>? UnlockRequested { get; set; }

    /// <summary>Number of failed attempts in this session of the card.</summary>
    public int FailureCount { get; private set; }

    /// <summary>File picker used by the keyfile buttons.</summary>
    public IFileDialogService Files { get; }

    /// <summary>True when a keyfile is attached.</summary>
    public bool HasKeyFile => !string.IsNullOrWhiteSpace(KeyFilePath);

    /// <summary>True when there is an error to show.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True when the save counter went backwards since this machine last saw the vault.</summary>
    public bool HasRollbackWarning => !string.IsNullOrEmpty(RollbackWarning);

    /// <summary>True when the unlock button is live.</summary>
    public bool CanSubmit => HasPassword && !IsDeriving;

    /// <summary>Points the card at a vault and describes what unlocking it will cost.</summary>
    /// <param name="path">Full path of the vault file.</param>
    /// <param name="kdf">Argon2id parameters from the header, when they are known.</param>
    /// <param name="keyFilePath">A remembered keyfile path, when the user opted in.</param>
    public void Configure(string path, KdfParameters? kdf, string? keyFilePath)
    {
        VaultPath = path;
        VaultName = System.IO.Path.GetFileNameWithoutExtension(path);
        KeyFilePath = keyFilePath;
        Error = null;
        StatusLine = null;
        RollbackWarning = null;
        FailureCount = 0;
        IsDeriving = false;

        HeaderLine = kdf is null
            ? "Argon2id · parameters read at unlock"
            : $"Argon2id · {kdf.MemoryKiB / 1024} MiB · {kdf.Iterations} passes · needs {kdf.MemoryKiB / 1024} MiB RAM";

        DerivingLabel = kdf is null
            ? "Deriving key · Argon2id"
            : $"Deriving key · Argon2id · {kdf.MemoryKiB / 1024} MiB · {kdf.Iterations} passes";

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reports what the vault looked like after a successful unlock.</summary>
    /// <param name="statistics">Statistics from the freshly opened session.</param>
    /// <param name="lastSeenCounter">Highest save counter this machine remembers, if any.</param>
    public void ReportOpened(VaultStatistics statistics, ulong? lastSeenCounter)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        string saved = statistics.LastSavedUtc is { } when
            ? when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "never";
        StatusLine = $"last saved {saved} · save #{statistics.SaveCounter}";

        RollbackWarning = lastSeenCounter is { } seen && statistics.SaveCounter < seen
            ? $"This machine last saw save #{seen}, but this file is save #{statistics.SaveCounter}. You may be looking at an older copy of the vault."
            : null;
    }

    /// <summary>Runs one unlock attempt with the credentials the view collected.</summary>
    /// <param name="password">The password; this method disposes it.</param>
    /// <param name="keyFile">The keyfile; this method disposes it.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<UnlockOutcome> SubmitAsync(Passphrase? password, KeyFile? keyFile, CancellationToken ct = default)
    {
        if (UnlockRequested is null)
        {
            password?.Dispose();
            keyFile?.Dispose();
            throw new InvalidOperationException("No unlock handler is attached.");
        }

        Error = null;
        IsDeriving = true;

        try
        {
            UnlockOutcome outcome = await UnlockRequested(password, keyFile, ct).ConfigureAwait(true);

            if (outcome == UnlockOutcome.Success)
            {
                FailureCount = 0;
                return outcome;
            }

            if (outcome != UnlockOutcome.Cancelled)
            {
                FailureCount++;
                Error = MessageFor(outcome);
                _log.Info($"Unlock attempt failed ({outcome}); attempt {FailureCount}.");

                if (FailureCount >= SoftDelayAfterFailures)
                {
                    // A soft delay, not a lockout: it costs an attacker time and costs a
                    // fat-fingered owner a second.
                    await Task.Delay(SoftDelay, ct).ConfigureAwait(true);
                }

                SelectPasswordRequested?.Invoke(this, EventArgs.Empty);
            }

            return outcome;
        }
        finally
        {
            password?.Dispose();
            keyFile?.Dispose();
            IsDeriving = false;
        }
    }

    /// <summary>Attaches a keyfile.</summary>
    [RelayCommand]
    public void ChooseKeyFile()
    {
        string? picked = Files.PickKeyFile();
        if (picked is not null)
        {
            KeyFilePath = picked;
        }
    }

    /// <summary>Removes the keyfile.</summary>
    [RelayCommand]
    public void RemoveKeyFile() => KeyFilePath = null;

    private static string MessageFor(UnlockOutcome outcome) => outcome switch
    {
        UnlockOutcome.WrongCredentials =>
            "That did not unlock the vault. The password, the keyfile, or the header itself is wrong — Bastion cannot tell which, and saying which would help an attacker.",
        UnlockOutcome.Damaged =>
            "The password is correct, but this vault has been altered or damaged since it was written. Do not save over it: export what you can with Recover first.",
        UnlockOutcome.NotAVault =>
            "This file is not a Bastion vault, or it was written by a newer version of the format.",
        UnlockOutcome.Unreadable =>
            "This file could not be read. It may be locked by another program, or on a drive that went away.",
        _ => "The vault could not be opened.",
    };
}
