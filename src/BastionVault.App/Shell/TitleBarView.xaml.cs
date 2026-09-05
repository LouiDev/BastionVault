using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Shell;

/// <summary>
/// The 40 px title bar: the lamp, the chamfered vault chip with its lock glyph and dirty bullet,
/// and the three caption buttons. The bar itself is caption area (WindowChrome drags it); only the
/// chip and the buttons opt back into hit testing.
/// </summary>
public partial class TitleBarView : UserControl
{
    private ShellViewModel? _shell;

    /// <summary>Creates the title bar.</summary>
    public TitleBarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateChip();
    }

    /// <summary>The maximise button, which the chrome behaviour hit-tests for Snap Layouts.</summary>
    public FrameworkElement MaximizeButton => MaximizeButtonElement;

    /// <summary>
    /// Raised just before the caption close button closes the window, so the host can record in
    /// the log where the close came from. The bar still closes the window itself.
    /// </summary>
    public event EventHandler? CloseButtonClicked;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellChanged;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellChanged;
        }

        UpdateChip();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Mode)
            or nameof(ShellViewModel.IsDirty)
            or nameof(ShellViewModel.Session)
            or nameof(ShellViewModel.VaultName)
            or null)
        {
            UpdateChip();
        }
    }

    private void UpdateChip()
    {
        if (_shell is null)
        {
            VaultChip.Visibility = Visibility.Collapsed;
            return;
        }

        bool hasVault = _shell.HasSession;
        VaultChip.Visibility = hasVault ? Visibility.Visible : Visibility.Collapsed;
        DirtyBullet.Visibility = _shell.IsDirty ? Visibility.Visible : Visibility.Collapsed;

        bool locked = _shell.Mode is ShellMode.Locked or ShellMode.Unlocking;
        LockGlyph.SetResourceReference(TextBlock.TextProperty, locked ? "Glyph.Lock" : "Glyph.Unlocked");
        LockGlyph.SetResourceReference(
            TextBlock.ForegroundProperty, locked ? "Brush.TextSecondary" : "Brush.Accent");
        VaultChip.SetResourceReference(
            Border.BorderBrushProperty, locked ? "Brush.StrokeControl" : "Brush.AccentDim");
    }

    private void OnShowPending(object sender, RoutedEventArgs e)
    {
        if (_shell?.PendingChanges is not { } pending)
        {
            return;
        }

        pending.Refresh();
        PendingPopup.IsOpen = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        CloseButtonClicked?.Invoke(this, EventArgs.Empty);
        Window.GetWindow(this)?.Close();
    }
}
