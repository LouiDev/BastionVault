using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bastion.App.Services;
using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Shell;

/// <summary>
/// The unlock card's code-behind. It owns the parts a view model may not touch: the
/// <see cref="PasswordBox"/>, the Caps Lock state, and turning the typed characters into a
/// <see cref="Passphrase"/> at the moment of submit (UI-CONTRACT.md section 1.3).
/// </summary>
public partial class UnlockView : UserControl
{
    private UnlockViewModel? _model;

    /// <summary>Creates the view.</summary>
    public UnlockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SelectPasswordRequested -= OnSelectPassword;
            _model.FocusRequested -= OnFocusRequested;
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as UnlockViewModel;

        if (_model is not null)
        {
            _model.SelectPasswordRequested += OnSelectPassword;
            _model.FocusRequested += OnFocusRequested;
            _model.PropertyChanged += OnModelChanged;
        }

        UpdateButton();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => PasswordField.Focus());
        }
        else
        {
            PasswordField.Clear();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UnlockViewModel.IsDeriving) or nameof(UnlockViewModel.DerivingLabel))
        {
            UpdateButton();
        }
    }

    private void UpdateButton()
    {
        if (_model is null)
        {
            return;
        }

        // The button is the progress indicator: it says what it is spending, not that it is busy.
        UnlockLabel.Text = _model.IsDeriving ? _model.DerivingLabel : "Unlock";
    }

    private void OnSelectPassword(object? sender, EventArgs e)
    {
        // Never clear after a failure: a typo in a long password should cost one keystroke.
        PasswordField.Focus();
        PasswordField.SelectAll();
    }

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        PasswordField.Clear();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => PasswordField.Focus());
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        _model.HasPassword = PasswordBoxBinder.HasContent(PasswordField);
        UpdateCapsBanner();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        UpdateCapsBanner();

        if (e.Key == Key.Enter && _model is { CanSubmit: true })
        {
            e.Handled = true;
            Submit();
        }
    }

    private void UpdateCapsBanner()
    {
        bool caps = Keyboard.IsKeyToggled(Key.CapsLock);
        CapsBanner.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        if (_model is not null)
        {
            _model.IsCapsLockOn = caps;
        }
    }

    private void OnUnlock(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        if (_model is not { CanSubmit: true } model)
        {
            return;
        }

        Passphrase? password = PasswordBoxBinder.ToPassphrase(PasswordField);
        KeyFile? keyFile = null;

        if (!string.IsNullOrWhiteSpace(model.KeyFilePath))
        {
            try
            {
                keyFile = KeyFile.Load(model.KeyFilePath);
            }
            catch (Exception ex) when (ex is VaultException or IOException or UnauthorizedAccessException or NotImplementedException)
            {
                password?.Dispose();
                model.Error = "That keyfile could not be read. Choose it again, or remove it.";
                return;
            }
        }

        _ = model.SubmitAsync(password, keyFile);
    }
}
