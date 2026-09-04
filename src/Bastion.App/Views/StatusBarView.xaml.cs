using System.Windows.Controls;

namespace Bastion.App.Views;

/// <summary>
/// The explorer's status bar: counts on the left, the running operation or the last thing that
/// happened in the middle, and on the right the vault's key-derivation parameters in the mono face
/// next to the pending-changes chip that opens the popover.
/// </summary>
public partial class StatusBarView : UserControl
{
    /// <summary>Creates the status bar.</summary>
    public StatusBarView() => InitializeComponent();
}
