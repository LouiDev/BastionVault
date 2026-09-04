using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bastion.App.Services;
using Bastion.App.ViewModels.Dialogs;
using Bastion.Core;

namespace Bastion.App.Dialogs;

/// <summary>
/// The New vault dialog's code-behind. It is the only place that touches the two password boxes:
/// it measures them, compares them, watches Caps Lock, drives hold-to-reveal, and builds the
/// <see cref="Passphrase"/> at the moment of submit.
/// </summary>
public partial class NewVaultDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public NewVaultDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordField.Focus();
    }

    private NewVaultDialogViewModel? Model => DataContext as NewVaultDialogViewModel;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        PasswordStrengthResult strength = PasswordBoxBinder.EstimateStrength(PasswordField);
        bool matches = PasswordBoxBinder.HasContent(PasswordField)
                       && PasswordBoxBinder.Matches(PasswordField, ConfirmField);

        model.ApplyPassword(strength, matches);

        MismatchLine.Visibility = !matches && PasswordBoxBinder.HasContent(ConfirmField)
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateCaps();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        UpdateCaps();

        if (e.Key == Key.Enter && Model is { CanCreate: true })
        {
            e.Handled = true;
            Submit();
        }
    }

    private void UpdateCaps()
    {
        bool caps = Keyboard.IsKeyToggled(Key.CapsLock);
        CapsBanner.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        if (Model is { } model)
        {
            model.IsCapsLockOn = caps;
        }
    }

    private void OnPresetLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: KdfPresetOption option } radio && Model is { } model)
        {
            radio.IsChecked = ReferenceEquals(option, model.SelectedPreset);
        }
    }

    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: KdfPresetOption option } && Model is { } model)
        {
            model.SelectedPreset = option;
        }
    }

    private void OnRevealDown(object sender, MouseButtonEventArgs e)
    {
        PasswordFieldHelper.Reveal(RevealHost, PasswordField);
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }
    }

    private void OnRevealUp(object sender, EventArgs e)
    {
        PasswordFieldHelper.Hide(RevealHost, PasswordField);
        if (sender is UIElement { IsMouseCaptured: true } element)
        {
            element.ReleaseMouseCapture();
        }
    }

    private void OnCreate(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        if (Model is not { CanCreate: true } model)
        {
            return;
        }

        PasswordFieldHelper.Hide(RevealHost, PasswordField);
        model.Submit(PasswordBoxBinder.ToPassphrase(PasswordField));
    }
}
