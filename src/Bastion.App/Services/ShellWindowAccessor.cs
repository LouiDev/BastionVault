using System.Windows;
using System.Windows.Interop;

namespace Bastion.App.Services;

/// <summary>
/// Holds the one <see cref="Window"/> the app owns, so the handful of services that genuinely
/// need an owner window (file pickers, capture exclusion, focus) can find it without reaching for
/// <c>Application.Current</c> and without any view model learning about windows.
/// </summary>
public sealed class ShellWindowAccessor
{
    /// <summary>The shell window, set once when it is created.</summary>
    public Window? Window { get; set; }

    /// <summary>The shell window's HWND, or <see cref="IntPtr.Zero"/> before it has a handle.</summary>
    public IntPtr Handle
    {
        get
        {
            if (Window is null)
            {
                return IntPtr.Zero;
            }

            var helper = new WindowInteropHelper(Window);
            return helper.Handle;
        }
    }
}
