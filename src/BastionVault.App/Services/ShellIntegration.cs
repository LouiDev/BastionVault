using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using Microsoft.Win32;

namespace BastionVault.App.Services;

/// <summary>
/// Windows shell integration. The <c>.bastion</c> association is written per user (HKCU) and only
/// ever from the Settings dialog - installing a file association behind the user's back is not
/// something a security tool should do. Process hygiene (AppUserModelID, empty jump list) runs at
/// start-up so Windows never records which vaults were opened (THREAT-MODEL.md A5).
/// </summary>
public sealed class ShellIntegration : IShellIntegration
{
    private const string AppUserModelId = "HMDSoftware.BastionVault";
    private const string Extension = ".bastion";
    private const string ProgId = "BastionVault.Vault.1";
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    private readonly ILog? _log;

    /// <summary>Creates the service.</summary>
    /// <param name="log">Optional log.</param>
    public ShellIntegration(ILog? log = null) => _log = log;

    /// <inheritdoc />
    public bool IsRegistered
    {
        get
        {
            try
            {
                using RegistryKey? extension = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}");
                if (extension?.GetValue(null) as string != ProgId)
                {
                    return false;
                }

                using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
                return command?.GetValue(null) is string value
                       && value.Contains(ExecutablePath(), StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void RegisterFileAssociation()
    {
        string exe = ExecutablePath();

        try
        {
            using (RegistryKey extension = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
            {
                extension.SetValue(null, ProgId);
            }

            using (RegistryKey progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progId.SetValue(null, "Bastion Vault");
                progId.SetValue("AppUserModelID", AppUserModelId);
                progId.SetValue("NoOpenWith", string.Empty);
            }

            using (RegistryKey icon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exe}\",0");
            }

            using (RegistryKey command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            {
                command.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            _log?.Info("Registered the .bastion file association for the current user.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Error("The .bastion file association could not be registered.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void UnregisterFileAssociation()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Extension}", throwOnMissingSubKey: false);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            _log?.Info("Removed the .bastion file association for the current user.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Error("The .bastion file association could not be removed.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void ApplyProcessHygiene()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (DllNotFoundException ex)
        {
            _log?.Warn("The AppUserModelID could not be set.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            _log?.Warn("The AppUserModelID could not be set.", ex);
        }

        if (Application.Current is not null)
        {
            // An empty jump list with both automatic categories off: Windows must not remember
            // which vaults were opened.
            var list = new JumpList
            {
                ShowRecentCategory = false,
                ShowFrequentCategory = false,
            };
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    private static string ExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "BastionVault.exe";
}
