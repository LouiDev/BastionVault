using System.Windows;
using System.Windows.Controls;

namespace BastionVault.App.Shell;

/// <summary>
/// The first-run screen (UI-CONTRACT.md section 7): one screen, three facts, shown once. It says
/// the uncomfortable thing out loud rather than burying it in a help page.
/// </summary>
public partial class FirstRunView : UserControl
{
    /// <summary>Creates the view.</summary>
    public FirstRunView() => InitializeComponent();

    /// <summary>Raised when the user acknowledges the screen.</summary>
    public event EventHandler? Acknowledged;

    private void OnContinue(object sender, RoutedEventArgs e) => Acknowledged?.Invoke(this, EventArgs.Empty);
}
