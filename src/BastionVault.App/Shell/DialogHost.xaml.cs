using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using BastionVault.App.ViewModels.Dialogs;

namespace BastionVault.App.Shell;

/// <summary>
/// Hosts in-window dialogs and makes them modal for real (UI-CONTRACT.md section 1.8): the shell's
/// content root is disabled while a dialog is up, the card is a focus scope with cycling tab
/// navigation, the first field takes focus, focus is restored on close, Escape cancels unless the
/// dialog says it may not, and the window refuses to close while a dialog is busy.
/// </summary>
public partial class DialogHost : UserControl
{
    /// <summary>Identifies the <see cref="CurrentDialog"/> dependency property.</summary>
    public static readonly DependencyProperty CurrentDialogProperty = DependencyProperty.Register(
        nameof(CurrentDialog), typeof(DialogViewModelBase), typeof(DialogHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="ContentRoot"/> dependency property.</summary>
    public static readonly DependencyProperty ContentRootProperty = DependencyProperty.Register(
        nameof(ContentRoot), typeof(UIElement), typeof(DialogHost), new PropertyMetadata(null));

    private readonly Stack<DialogViewModelBase> _stack = new();
    private readonly Stack<IInputElement?> _focusToRestore = new();

    /// <summary>Creates the host.</summary>
    public DialogHost()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>The dialog currently on top, or <see langword="null"/> when none is open.</summary>
    public DialogViewModelBase? CurrentDialog
    {
        get => (DialogViewModelBase?)GetValue(CurrentDialogProperty);
        private set => SetValue(CurrentDialogProperty, value);
    }

    /// <summary>The element to disable while a dialog is open: the shell's content root.</summary>
    public UIElement? ContentRoot
    {
        get => (UIElement?)GetValue(ContentRootProperty);
        set => SetValue(ContentRootProperty, value);
    }

    /// <summary>True while any open dialog is doing work that must not be interrupted.</summary>
    public bool IsBusy => _stack.Any(d => d.IsBusy);

    /// <summary>True while at least one dialog is open.</summary>
    public bool IsOpen => _stack.Count > 0;

    /// <summary>
    /// Shows a dialog and completes when it closes. Nested calls stack: an error raised while a
    /// progress card is up appears over it and returns first.
    /// </summary>
    /// <typeparam name="TResult">What the dialog produces.</typeparam>
    /// <param name="dialog">The dialog view model.</param>
    /// <param name="ct">Cancels the dialog from the caller's side.</param>
    public async Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> dialog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        Push(dialog);

        void OnClosed(object? sender, object? result) => Pop(dialog);
        dialog.Closed += OnClosed;

        using CancellationTokenRegistration registration = ct.CanBeCanceled
            ? ct.Register(() => Dispatcher.BeginInvoke(dialog.Cancel))
            : default;

        try
        {
            return await dialog.Result.ConfigureAwait(true);
        }
        finally
        {
            dialog.Closed -= OnClosed;
        }
    }

    /// <summary>
    /// Picks what a freshly shown dialog should focus. A dialog view docks its button row before
    /// its scrolling content, so the first element in visual order is Cancel - and a keyboard user
    /// who opened New vault and pressed Enter cancelled it. A field is therefore preferred over a
    /// button; a button is only used when the dialog has no field at all (Confirm, About).
    /// </summary>
    /// <param name="root">Subtree to search.</param>
    private static IInputElement? FindInitialFocus(DependencyObject root)
    {
        List<UIElement> candidates = [];
        Collect(root, candidates);

        foreach (UIElement candidate in candidates)
        {
            if (candidate is not System.Windows.Controls.Primitives.ButtonBase)
            {
                return candidate;
            }
        }

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static void Collect(DependencyObject root, List<UIElement> found)
    {
        if (root is UIElement element && element.Focusable && element.IsEnabled && element.IsVisible)
        {
            found.Add(element);
            return;
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            Collect(System.Windows.Media.VisualTreeHelper.GetChild(root, i), found);
        }
    }

    private static bool CanTakeFocus(IInputElement? candidate) =>
        candidate is UIElement { IsVisible: true, IsEnabled: true, Focusable: true }
        or System.Windows.ContentElement { IsEnabled: true, Focusable: true };

    private void Push(DialogViewModelBase dialog)
    {
        _focusToRestore.Push(Keyboard.FocusedElement);
        _stack.Push(dialog);
        CurrentDialog = dialog;

        if (ContentRoot is not null)
        {
            ContentRoot.IsEnabled = false;
        }

        Visibility = Visibility.Visible;
        Animate(open: true);

        // Let the template materialise before hunting for the first field.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            Card.UpdateLayout();

            // A view that focuses its own field on Loaded - the three password surfaces do - has
            // already made a better choice than any tree walk can.
            if (Presenter.IsKeyboardFocusWithin)
            {
                return;
            }

            IInputElement? first = FindInitialFocus(Presenter);
            if (first is not null)
            {
                Keyboard.Focus(first);
            }
            else
            {
                Card.Focusable = true;
                Keyboard.Focus(Card);
            }
        });
    }

    private void Pop(DialogViewModelBase dialog)
    {
        if (_stack.Count == 0)
        {
            return;
        }

        // Dialogs close in order; a defensive filter keeps a stray close from unwinding the stack.
        if (!ReferenceEquals(_stack.Peek(), dialog))
        {
            var kept = _stack.Where(d => !ReferenceEquals(d, dialog)).Reverse().ToArray();
            _stack.Clear();
            foreach (DialogViewModelBase item in kept)
            {
                _stack.Push(item);
            }
        }
        else
        {
            _stack.Pop();
        }

        IInputElement? restore = _focusToRestore.Count > 0 ? _focusToRestore.Pop() : null;

        if (_stack.Count > 0)
        {
            // A nested dialog closed over its parent. Without this the focus sits on an element
            // that has just left the visual tree, focus falls back to the window, and the host's
            // PreviewKeyDown - which only fires while focus is inside it - stops seeing Escape
            // and Enter for the dialog that is still up.
            CurrentDialog = _stack.Peek();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                Card.UpdateLayout();
                IInputElement? target = CanTakeFocus(restore) ? restore : FindInitialFocus(Presenter);
                if (target is not null)
                {
                    Keyboard.Focus(target);
                }
            });

            return;
        }

        CurrentDialog = null;
        if (ContentRoot is not null)
        {
            ContentRoot.IsEnabled = true;
        }

        Visibility = Visibility.Collapsed;

        // Only restore focus to something that can still take it. The unlock card's password box
        // is collapsed by the time its progress dialog closes, and focusing it dropped focus onto
        // the window, which left the whole explorer keymap dead until the user clicked a row.
        if (CanTakeFocus(restore))
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (CanTakeFocus(restore))
                {
                    Keyboard.Focus(restore);
                }
            });
        }
    }

    private void Animate(bool open)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Root.Opacity = 1;
            CardLift.Y = 0;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var fade = new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, duration) { EasingFunction = ease };
        var lift = new DoubleAnimation(open ? 8 : 0, open ? 0 : 8, duration) { EasingFunction = ease };

        Root.BeginAnimation(OpacityProperty, fade);
        CardLift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, lift);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CurrentDialog is not { } dialog)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                if (dialog.CanClose)
                {
                    dialog.Cancel();
                }

                e.Handled = true;
                break;

            case Key.Enter:
                // A multi-line editor and an explicitly default-less dialog keep Enter.
                if (Keyboard.FocusedElement is TextBox { AcceptsReturn: true })
                {
                    return;
                }

                if (dialog.Accept())
                {
                    e.Handled = true;
                }

                break;

            default:
                break;
        }
    }
}
