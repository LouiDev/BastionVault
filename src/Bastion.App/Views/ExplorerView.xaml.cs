using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Bastion.App.Input;
using Bastion.App.Services;
using Bastion.App.ViewModels;

namespace Bastion.App.Views;

/// <summary>
/// The explorer's root. The view model owns every decision; this file owns the three things a view
/// model may not touch: keyboard routing built from <see cref="KeyMap"/>, focus movement between
/// the panes, and the <c>RowHeight</c> resource that the density setting swaps.
/// </summary>
public partial class ExplorerView : UserControl
{
    private readonly List<Binding> _bindings = [];

    private ExplorerViewModel? _explorer;
    private Window? _window;
    private double _previewWidth = 320;

    /// <summary>Creates the explorer view.</summary>
    public ExplorerView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (DataContext is not ExplorerViewModel explorer)
        {
            return;
        }

        _explorer = explorer;
        explorer.SearchFocusRequested += OnSearchFocusRequested;
        explorer.FocusCycleRequested += OnFocusCycleRequested;
        explorer.PropertyChanged += OnExplorerPropertyChanged;

        BuildKeyBindings(explorer);
        ApplyDensity(explorer.Density);
        ApplyPreviewVisibility(explorer.IsPreviewVisible);

        // Loaded is not enough. The shell materialises this view once, at window load, with a null
        // Explorer and a hidden, empty list; it never fires again when a vault is opened into the
        // same instance. Without a focus request here the caret stays on the window after unlock
        // and every Explorer-scope shortcut - arrows, typeahead, F2, Delete, Ctrl+Shift+N - is
        // dead until the user clicks a row.
        RequestListFocus();
    }

    /// <summary>
    /// Moves keyboard focus into the entry list once it is worth focusing. The list is still empty
    /// at the moment a vault opens, so the attempt is repeated one dispatcher turn later at a
    /// lower priority, by which time the first refresh has populated it.
    /// </summary>
    private void RequestListFocus() => TryFocusList(attempts: 3);

    private void TryFocusList(int attempts)
    {
        if (attempts <= 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (_explorer is null || !IsVisible || IsKeyboardFocusWithin)
            {
                // Either there is nothing to focus yet, or the user is already in here and must
                // not have the caret pulled out from under them.
                if (_explorer is not null && !IsKeyboardFocusWithin)
                {
                    TryFocusList(attempts - 1);
                }

                return;
            }

            List.FocusList();

            if (!IsKeyboardFocusWithin)
            {
                TryFocusList(attempts - 1);
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.Activated += OnWindowActivated;
                _window.Deactivated += OnWindowDeactivated;
            }
        }

        if (_explorer is not null)
        {
            _explorer.IsWindowActive = _window?.IsActive ?? true;
        }

        RequestListFocus();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RequestListFocus();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
    }

    private void Detach()
    {
        if (_explorer is null)
        {
            return;
        }

        _explorer.SearchFocusRequested -= OnSearchFocusRequested;
        _explorer.FocusCycleRequested -= OnFocusCycleRequested;
        _explorer.PropertyChanged -= OnExplorerPropertyChanged;
        _explorer = null;
        _bindings.Clear();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_explorer is not null)
        {
            _explorer.IsWindowActive = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_explorer is not null)
        {
            _explorer.IsWindowActive = false;
        }
    }

    private void OnExplorerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_explorer is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ExplorerViewModel.Density):
                ApplyDensity(_explorer.Density);
                break;

            case nameof(ExplorerViewModel.IsPreviewVisible):
                ApplyPreviewVisibility(_explorer.IsPreviewVisible);
                break;

            default:
                break;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) => CommandBar.FocusSearch();

    private void ApplyDensity(RowDensity density)
    {
        double height = density switch
        {
            RowDensity.Compact => 24d,
            RowDensity.Spacious => 32d,
            _ => 28d,
        };

        // The theme reads RowHeight as a DynamicResource, so one number re-styles both the list
        // and the tree without touching a single template.
        Resources["RowHeight"] = height;
    }

    private void ApplyPreviewVisibility(bool visible)
    {
        if (!visible && PreviewColumn.ActualWidth > 0)
        {
            _previewWidth = PreviewColumn.ActualWidth;
        }

        Preview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.MinWidth = visible ? 200 : 0;
        PreviewColumn.Width = visible ? new GridLength(Math.Max(200, _previewWidth)) : new GridLength(0);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    /// <summary>Turns every Explorer-scope keymap row into a live binding.</summary>
    /// <param name="explorer">The explorer whose commands the keys run.</param>
    private void BuildKeyBindings(ExplorerViewModel explorer)
    {
        _bindings.Clear();

        foreach (ShortcutEntry entry in KeyMap.Entries)
        {
            if (entry.Scope != ShortcutScope.Explorer)
            {
                continue;
            }

            if (!explorer.ShortcutCommands.TryGetValue(entry.Id, out ICommand? command))
            {
                continue;
            }

            foreach (Chord chord in entry.Chords)
            {
                _bindings.Add(new Binding(chord, command, entry.Id));
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_explorer is null || e.Handled)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool inTextEditor = Keyboard.FocusedElement is TextBoxBase or PasswordBox;

        foreach (Binding binding in _bindings)
        {
            if (binding.Chord.Key != key || binding.Chord.Modifiers != modifiers)
            {
                continue;
            }

            // A text editor owns the gestures a user types text with, and nothing else. The old
            // rule let only Alt gestures and F6 past, which killed every other command while the
            // search box had focus - typing a query and pressing Ctrl+Shift+I did nothing.
            if (inTextEditor && BelongsToTextEditor(binding.Chord))
            {
                return;
            }

            object? parameter = binding.Id == "CycleFocus" && modifiers.HasFlag(ModifierKeys.Shift)
                ? "back"
                : null;

            bool can = binding.Command.CanExecute(parameter);
            _explorer.LogShortcut(binding.Id, can);

            if (!can)
            {
                return;
            }

            binding.Command.Execute(parameter);
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// Whether a focused text box owns <paramref name="chord"/>. The editing gestures do: Escape
    /// cancels a rename rather than clearing the search, Enter commits it, F2 must not restart it,
    /// Delete/Space/Backspace edit the text, the context-menu keys open the text box's own menu,
    /// and Ctrl+Z/Y/X/C/V/A are the clipboard and undo of the editor. Everything else in the
    /// keymap - the imports, Export, New folder, Copy path, Panic, the address bar and search
    /// gestures, F6 and the Alt navigation - means nothing inside a text box and reaches the
    /// explorer instead.
    /// </summary>
    /// <param name="chord">The chord that matched.</param>
    /// <returns><see langword="true"/> when the key must be left to the editor.</returns>
    internal static bool BelongsToTextEditor(Chord chord) => chord.Modifiers switch
    {
        ModifierKeys.None or ModifierKeys.Shift =>
            chord.Key is Key.Escape or Key.Return or Key.Delete or Key.Space or Key.Back
                or Key.F2 or Key.F10 or Key.Apps,
        ModifierKeys.Control =>
            chord.Key is Key.Z or Key.Y or Key.X or Key.C or Key.V or Key.A,
        _ => false,
    };

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_explorer is null)
        {
            return;
        }

        switch (e.ChangedButton)
        {
            case MouseButton.XButton1 when _explorer.BackCommand.CanExecute(null):
                _explorer.BackCommand.Execute(null);
                e.Handled = true;
                break;

            case MouseButton.XButton2 when _explorer.ForwardCommand.CanExecute(null):
                _explorer.ForwardCommand.Execute(null);
                e.Handled = true;
                break;

            default:
                break;
        }
    }

    // ── Focus ─────────────────────────────────────────────────────────────────

    private void OnFocusCycleRequested(object? sender, bool backwards)
    {
        int current = CurrentPane();
        int count = 4;
        int next = ((current + (backwards ? -1 : 1)) % count + count) % count;

        // Skip the preview when it is hidden.
        if (next == 3 && Preview.Visibility != Visibility.Visible)
        {
            next = backwards ? 2 : 0;
        }

        switch (next)
        {
            case 0:
                Tree.FocusTree();
                break;

            case 1:
                List.FocusList();
                break;

            case 2:
                AddressBar.FocusPathBox();
                break;

            default:
                Preview.Focus();
                break;
        }
    }

    private int CurrentPane()
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
        {
            return -1;
        }

        for (DependencyObject? node = focused; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, Tree))
            {
                return 0;
            }

            if (ReferenceEquals(node, List))
            {
                return 1;
            }

            if (ReferenceEquals(node, AddressBar))
            {
                return 2;
            }

            if (ReferenceEquals(node, Preview))
            {
                return 3;
            }
        }

        return -1;
    }

    /// <summary>One key gesture bound to one command, built from the keymap.</summary>
    /// <param name="Chord">The gesture.</param>
    /// <param name="Command">What it runs.</param>
    /// <param name="Id">Keymap identifier, used to shape the command parameter.</param>
    private sealed record Binding(Chord Chord, ICommand Command, string Id);
}
