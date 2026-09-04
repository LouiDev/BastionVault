using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BastionVault.App.Services;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;

namespace BastionVault.App.Dialogs;

/// <summary>
/// The Change credentials dialog's code-behind: the three password boxes, Caps Lock,
/// hold-to-reveal, and building both passphrases at the moment of submit.
/// </summary>
public partial class ChangeCredentialsDialogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ChangeCredentialsDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => CurrentField.Focus();
    }

    private ChangeCredentialsDialogViewModel? Model => DataContext as ChangeCredentialsDialogViewModel;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        PasswordStrengthResult strength = PasswordBoxBinder.EstimateStrength(NewField);
        bool matches = PasswordBoxBinder.HasContent(NewField)
                       && PasswordBoxBinder.Matches(NewField, ConfirmField);

        model.ApplyPassword(strength, matches, PasswordBoxBinder.HasContent(CurrentField));

        bool caps = Keyboard.IsKeyToggled(Key.CapsLock);
        CapsBanner.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        model.IsCapsLockOn = caps;
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

    private void OnModeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } radio && Model is { } model)
        {
            radio.IsChecked = tag == model.Mode.ToString();
        }
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Model is { } model
            && Enum.TryParse(tag, out CredentialChangeMode mode))
        {
            model.Mode = mode;
        }
    }

    private void OnRevealDown(object sender, MouseButtonEventArgs e)
    {
        PasswordFieldHelper.Reveal(RevealHost, NewField);
        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }
    }

    private void OnRevealUp(object sender, EventArgs e)
    {
        PasswordFieldHelper.Hide(RevealHost, NewField);
        if (sender is UIElement { IsMouseCaptured: true } element)
        {
            element.ReleaseMouseCapture();
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanApply: true } model)
        {
            return;
        }

        PasswordFieldHelper.Hide(RevealHost, NewField);
        Passphrase? current = PasswordBoxBinder.ToPassphrase(CurrentField);
        Passphrase? updated = PasswordBoxBinder.ToPassphrase(NewField);
        model.Submit(current, updated);
    }
}
