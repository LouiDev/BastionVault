using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BastionVault.App.Input;
using BastionVault.App.Services;
using BastionVault.App.ViewModels;

namespace BastionVault.App.Shell;

/// <summary>
/// The one <see cref="Window"/> Bastion Vault has. It wires the chrome behaviour, turns the global rows
/// of <see cref="KeyMap"/> into input bindings, restores and records the window placement, and
/// owns the close prompt.
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly ISettingsService _settings;
    private readonly ILog _log;
    private WindowChromeBehavior? _chrome;
    private bool _closeConfirmed;

    /// <summary>Creates the shell window.</summary>
    /// <param name="shell">The shell view model.</param>
    /// <param name="settings">Settings, for the window placement.</param>
    /// <param name="log">Log.</param>
    public ShellWindow(ShellViewModel shell, ISettingsService settings, ILog log)
    {
        ArgumentNullException.ThrowIfNull(shell);

        _shell = shell;
        _settings = settings;
        _log = log;

        InitializeComponent();

        DataContext = shell;
        _chrome = WindowChromeBehavior.Attach(this, ContentRoot, TitleBar.MaximizeButton);

        BindGlobalShortcuts();
        RestorePlacement();

        shell.CloseRequested += (_, _) => Close();
        shell.FirstRunRequested += (_, _) => ShowFirstRun();
        FirstRun.Acknowledged += OnFirstRunAcknowledged;

        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>The dialog host, handed to <see cref="DialogService"/> by the composition root.</summary>
    public DialogHost DialogHost => Dialogs;

    private void BindGlobalShortcuts()
    {
        foreach (ShortcutEntry entry in KeyMap.Entries.Where(e => e.Scope == ShortcutScope.Global))
        {
            ICommand? command = CommandFor(entry.Id);
            if (command is null)
            {
                continue;
            }

            foreach (Chord chord in entry.Chords)
            {
                InputBindings.Add(new KeyBinding(command, chord.Key, chord.Modifiers));
            }
        }
    }

    private ICommand? CommandFor(string id) => id switch
    {
        KeyMap.NewVault => _shell.NewVaultCommand,
        KeyMap.OpenVault => _shell.OpenVaultCommand,
        KeyMap.Save => _shell.SaveCommand,
        KeyMap.SaveCopy => _shell.SaveCopyCommand,
        KeyMap.Lock => _shell.LockCommand,
        KeyMap.Verify => _shell.VerifyCommand,
        KeyMap.ChangeCredentials => _shell.ChangeCredentialsCommand,
        KeyMap.Settings => _shell.ShowSettingsCommand,
        KeyMap.Shortcuts => _shell.ShowShortcutsCommand,
        _ => null,
    };

    private void ShowFirstRun() => FirstRun.Visibility = Visibility.Visible;

    private void OnFirstRunAcknowledged(object? sender, EventArgs e)
    {
        FirstRun.Visibility = Visibility.Collapsed;
        _settings.Current.ShowFirstRun = false;
        _settings.Save();
    }

    private void RestorePlacement()
    {
        WindowPlacement placement = _settings.Current.WindowPlacement;
        if (!placement.HasValue)
        {
            return;
        }

        // A placement is only restored when it still lands on a monitor that exists.
        var rect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (rect.Width < MinWidth || rect.Height < MinHeight || !virtualScreen.IntersectsWith(rect))
        {
            _log.Info("The saved window placement no longer fits the monitors; the default is used.");
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void RecordPlacement()
    {
        WindowPlacement placement = _settings.Current.WindowPlacement;
        placement.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            placement.Left = Left;
            placement.Top = Top;
            placement.Width = Width;
            placement.Height = Height;
        }
        else
        {
            Rect restore = RestoreBounds;
            if (restore != Rect.Empty)
            {
                placement.Left = restore.Left;
                placement.Top = restore.Top;
                placement.Width = restore.Width;
                placement.Height = restore.Height;
            }
        }

        placement.HasValue = true;
        _settings.Save();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        // A dialog that is mid-operation owns the window until it is done (UI-CONTRACT section 1.8).
        if (Dialogs.IsBusy)
        {
            e.Cancel = true;
            return;
        }

        // This close is always refused and then re-issued. Both awaits below finish synchronously
        // whenever there is nothing to ask the user about - a clean vault, the usual case - and
        // WPF refuses a Close() made from inside its own Closing event with
        // "Cannot ... while a Window is closing". So the real close is always posted.
        e.Cancel = true;

        try
        {
            if (!await _shell.RequestCloseAsync().ConfigureAwait(true))
            {
                return;
            }

            RecordPlacement();
            await _shell.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Closing must never be the thing that crashes the app with a vault open.
            _log.Error("Closing failed; the keys are zeroed and the window is closed anyway.", ex);
            _shell.ZeroKeys();
        }

        _closeConfirmed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _chrome?.Dispose();
        _chrome = null;
    }
}
