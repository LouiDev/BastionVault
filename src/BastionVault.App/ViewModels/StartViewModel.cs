using BastionVault.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BastionVault.App.ViewModels;

/// <summary>
/// The no-vault screen: two verbs and the vaults you had open before. It is the only screen that
/// is allowed to look empty, so it carries the blueprint vault door and one line of copy that
/// says what a vault is (UI-CONTRACT.md section 7).
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    private readonly IRecentVaults _recent;

    [ObservableProperty]
    private IReadOnlyList<RecentVault> _recents = [];

    /// <summary>Creates the screen.</summary>
    /// <param name="recent">The recent-vault list.</param>
    /// <param name="createVault">Command that creates a new vault.</param>
    /// <param name="openVault">Command that opens an existing vault.</param>
    /// <param name="openRecent">Command that opens one recent vault, taking its path.</param>
    public StartViewModel(
        IRecentVaults recent,
        IAsyncRelayCommand createVault,
        IAsyncRelayCommand openVault,
        IAsyncRelayCommand<string> openRecent)
    {
        ArgumentNullException.ThrowIfNull(recent);

        _recent = recent;
        CreateVaultCommand = createVault;
        OpenVaultCommand = openVault;
        OpenRecentCommand = openRecent;

        _recent.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Creates a new vault.</summary>
    public IAsyncRelayCommand CreateVaultCommand { get; }

    /// <summary>Opens an existing vault through the file picker.</summary>
    public IAsyncRelayCommand OpenVaultCommand { get; }

    /// <summary>Opens one entry of the recents list.</summary>
    public IAsyncRelayCommand<string> OpenRecentCommand { get; }

    /// <summary>True when there is anything in the recents list.</summary>
    public bool HasRecents => Recents.Count > 0;

    /// <summary>The single line of copy under the two buttons.</summary>
    public string Tagline =>
        "A vault is one encrypted file. Everything you put in it lives there, and nothing leaves it unless you export it.";

    /// <summary>Re-reads the recents list.</summary>
    public void Refresh()
    {
        Recents = _recent.Items;
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <summary>Drops one vault from the recents list.</summary>
    /// <param name="path">Full path of the vault to forget.</param>
    [RelayCommand]
    public void Forget(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _recent.Forget(path);
        }
    }

    /// <summary>Empties the recents list.</summary>
    [RelayCommand]
    public void ClearRecents() => _recent.Clear();
}
