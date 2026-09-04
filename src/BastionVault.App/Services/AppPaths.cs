using System.IO;
namespace BastionVault.App.Services;

/// <summary>
/// The three places Bastion Vault writes to under <c>%LOCALAPPDATA%\BastionVault</c>. Nothing here ever
/// holds vault content: settings are plain JSON, the recent list and the rollback record are
/// DPAPI-protected, and the log is scrubbed by contract (UI-CONTRACT.md section 1.13).
/// </summary>
public static class AppPaths
{
    /// <summary>Root of the per-user data directory; created on first use.</summary>
    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BastionVault");

    /// <summary>Full path of <c>settings.json</c>.</summary>
    public static string SettingsFile => System.IO.Path.Combine(Root, "settings.json");

    /// <summary>Full path of the DPAPI-protected recent-vault list.</summary>
    public static string RecentFile => System.IO.Path.Combine(Root, "recent.dat");

    /// <summary>Full path of the DPAPI-protected rollback record.</summary>
    public static string RollbackFile => System.IO.Path.Combine(Root, "rollback.dat");

    /// <summary>Directory holding the rolling log files.</summary>
    public static string LogDirectory => System.IO.Path.Combine(Root, "logs");

    /// <summary>Creates <paramref name="directory"/> when it does not exist yet.</summary>
    /// <param name="directory">Directory to ensure.</param>
    public static void Ensure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
