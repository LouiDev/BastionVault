using System.Windows.Controls;

namespace BastionVault.App.Shell;

/// <summary>
/// The no-vault screen. It has no logic of its own: everything it shows comes from
/// <see cref="ViewModels.StartViewModel"/>.
/// </summary>
public partial class StartView : UserControl
{
    /// <summary>Creates the view.</summary>
    public StartView() => InitializeComponent();
}
