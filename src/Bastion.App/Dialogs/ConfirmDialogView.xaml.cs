using System.Windows;
using System.Windows.Controls;
using Bastion.App.ViewModels.Dialogs;

namespace Bastion.App.Dialogs;

/// <summary>
/// Confirmation, information and error. The primary verb is styled danger and loses its default
/// status when it destroys something, so a destructive answer always costs an aimed click.
/// </summary>
public partial class ConfirmDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ConfirmDialogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not ConfirmDialogViewModel model)
        {
            return;
        }

        (string glyph, string brush) = model.Kind switch
        {
            MessageKind.Error => ("Glyph.Error", "Brush.DangerText"),
            MessageKind.Information => ("Glyph.Info", "Brush.Info"),
            _ => ("Glyph.Warning", "Brush.Warning"),
        };

        KindGlyph.SetResourceReference(TextBlock.TextProperty, glyph);
        KindGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);

        PrimaryButton.SetResourceReference(
            StyleProperty, model.IsDestructive ? "Button.Danger" : "Button.Primary");
        PrimaryButton.IsDefault = !model.IsDestructive;
    }
}
