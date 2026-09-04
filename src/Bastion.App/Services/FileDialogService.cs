using Microsoft.Win32;

namespace Bastion.App.Services;

/// <summary>
/// The OS file pickers, always shown with the shell window as owner. <c>DereferenceLinks</c> is
/// off so a shortcut is never silently resolved, and Bastion adds nothing to the Windows recent
/// items itself (THREAT-MODEL.md A5 - the pickers' own MRU is outside our control and is listed
/// as a residual trace).
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    private const string VaultFilter = "Bastion vault (*.bastion)|*.bastion|All files (*.*)|*.*";
    private const string KeyFileFilter = "Bastion keyfile (*.key)|*.key|All files (*.*)|*.*";

    private readonly ShellWindowAccessor _shell;

    /// <summary>Creates the service.</summary>
    /// <param name="shell">Source of the owner window.</param>
    public FileDialogService(ShellWindowAccessor shell) => _shell = shell;

    /// <inheritdoc />
    public string? PickVaultToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open vault",
            Filter = VaultFilter,
            DefaultExt = ".bastion",
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickVaultToCreate(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "New vault",
            Filter = VaultFilter,
            DefaultExt = ".bastion",
            FileName = string.IsNullOrWhiteSpace(suggestedName) ? "Vault.bastion" : suggestedName + ".bastion",
            OverwritePrompt = true,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickKeyFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose keyfile",
            Filter = KeyFileFilter,
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? PickKeyFileToCreate()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Generate keyfile",
            Filter = KeyFileFilter,
            DefaultExt = ".key",
            FileName = "bastion.key",
            OverwritePrompt = true,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> PickFilesToImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import files",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FileNames : [];
    }

    /// <inheritdoc />
    public string? PickFolderToImport()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Import folder",
            Multiselect = false,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FolderName : null;
    }

    /// <inheritdoc />
    public string? PickExportFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Export to folder",
            Multiselect = false,
            DereferenceLinks = false,
            AddToRecent = false,
        };

        return Show(dialog) ? dialog.FolderName : null;
    }

    private bool Show(CommonDialog dialog) =>
        _shell.Window is { } owner ? dialog.ShowDialog(owner) == true : dialog.ShowDialog() == true;
}
