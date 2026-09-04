using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bastion.App.Dialogs;

/// <summary>
/// Hold-to-reveal for a <see cref="PasswordBox"/>, implemented exactly as UI-CONTRACT.md section 1.3
/// requires: a <see cref="TextBox"/> that exists only while the button is held, is filled from the
/// secure string, has undo turned off, and is cleared and thrown away on release.
/// </summary>
/// <remarks>
/// Filling the text box means the characters exist as a managed string for as long as they are on
/// screen. That is unavoidable for any reveal feature and is the same residual risk
/// THREAT-MODEL.md A6 already lists for "text shown on screen"; nothing else in Bastion ever
/// materialises a password.
/// </remarks>
public static class PasswordFieldHelper
{
    /// <summary>Fills <paramref name="host"/> with a temporary read-only text box showing the password.</summary>
    /// <param name="host">Container that overlays the password box.</param>
    /// <param name="source">The password box to reveal.</param>
    public static void Reveal(Border host, PasswordBox source)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(source);

        Hide(host);

        var box = new TextBox
        {
            IsUndoEnabled = false,
            IsReadOnly = true,
            IsTabStop = false,
            Focusable = false,
            BorderThickness = source.BorderThickness,
            Padding = source.Padding,
            Height = source.ActualHeight > 0 ? source.ActualHeight : source.Height,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = ReadPlainText(source),
        };

        box.SetResourceReference(FrameworkElement.StyleProperty, "TextBox.Default");
        host.Child = box;
        host.Visibility = Visibility.Visible;
        source.Visibility = Visibility.Hidden;
    }

    /// <summary>Clears and removes the temporary text box.</summary>
    /// <param name="host">Container that held it.</param>
    /// <param name="source">The password box to show again.</param>
    public static void Hide(Border host, PasswordBox? source = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (host.Child is TextBox box)
        {
            box.Clear();
            box.Text = string.Empty;
        }

        host.Child = null;
        host.Visibility = Visibility.Collapsed;

        if (source is not null)
        {
            source.Visibility = Visibility.Visible;
        }
    }

    private static string ReadPlainText(PasswordBox source)
    {
        using SecureString secure = source.SecurePassword;
        IntPtr bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToCoTaskMemUnicode(secure);
            return Marshal.PtrToStringUni(bstr, secure.Length);
        }
        finally
        {
            if (bstr != IntPtr.Zero)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(bstr);
            }
        }
    }
}
