using System.Runtime.InteropServices;

namespace BastionVault.App.Services;

/// <summary>
/// Keeps the shell window out of screen captures, screen sharing and Recall by setting
/// <c>WDA_EXCLUDEFROMCAPTURE</c>. On by default; the Settings dialog turns it off for a user who
/// needs to share their screen. The affinity is dropped on lock, because a lock screen is safe to
/// capture (UI-CONTRACT.md section 1.10).
/// </summary>
public sealed class ScreenPrivacy : IScreenPrivacy
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private readonly ShellWindowAccessor _shell;
    private readonly ILog? _log;
    private bool _wanted;

    /// <summary>Creates the service.</summary>
    /// <param name="shell">Source of the shell window handle.</param>
    /// <param name="log">Optional log.</param>
    public ScreenPrivacy(ShellWindowAccessor shell, ILog? log = null)
    {
        _shell = shell;
        _log = log;
    }

    /// <inheritdoc />
    public void SetExcludeFromCapture(bool exclude)
    {
        _wanted = exclude;
        Apply();
    }

    /// <summary>Re-applies the last requested state; called after the window gets a new handle.</summary>
    public void Reapply() => Apply();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private void Apply()
    {
        IntPtr handle = _shell.Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!SetWindowDisplayAffinity(handle, _wanted ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
        {
            _log?.Warn($"Display affinity could not be set (error {Marshal.GetLastWin32Error()}).");
        }
    }
}
