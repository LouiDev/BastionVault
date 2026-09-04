using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BastionVault.App.Views;

/// <summary>
/// The explorer's command bar: content commands on the left, search in the middle, view and vault
/// commands on the right. Every tooltip reads "Name (Shortcut)" and the shortcut comes from the
/// keymap, so the bar cannot promise a gesture nothing binds.
/// </summary>
/// <remarks>
/// The bar is responsive. Column 0 is <c>Auto</c>, so with every label drawn it asks for more than
/// the window's declared minimum width can give and the right-hand group (Save, Verify, Lock) was
/// simply clipped off the end, while the search field - inside a star column measured below its
/// own MinWidth - was clipped to nothing yet stayed focusable. Two breakpoints fix both: below
/// <see cref="LabelBreakpoint"/> the labelled buttons fall back to glyphs, which frees enough room
/// for the search field and the whole right-hand group; below <see cref="SearchBreakpoint"/> the
/// search field is collapsed outright rather than painted as an invisible but tabbable box.
/// </remarks>
public partial class CommandBarView : UserControl
{
    /// <summary>Below this width the four labelled buttons drop to glyphs.</summary>
    public const double LabelBreakpoint = 1160;

    /// <summary>Below this width the search field is collapsed instead of clipped.</summary>
    public const double SearchBreakpoint = 720;

    /// <summary>Identifies the <see cref="ShowLabels"/> dependency property.</summary>
    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(CommandBarView), new PropertyMetadata(true));

    /// <summary>Identifies the <see cref="ShowSearch"/> dependency property.</summary>
    public static readonly DependencyProperty ShowSearchProperty = DependencyProperty.Register(
        nameof(ShowSearch), typeof(bool), typeof(CommandBarView), new PropertyMetadata(true));

    /// <summary>Creates the command bar.</summary>
    public CommandBarView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>True when the bar is wide enough to draw the button labels.</summary>
    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    /// <summary>True when the bar is wide enough to draw the search field.</summary>
    public bool ShowSearch
    {
        get => (bool)GetValue(ShowSearchProperty);
        set => SetValue(ShowSearchProperty, value);
    }

    /// <summary>Which tier a given bar width belongs to. Exposed so a test can pin the rules.</summary>
    /// <param name="width">Width available to the bar, in DIP.</param>
    /// <returns>Whether labels and the search field fit.</returns>
    public static (bool Labels, bool Search) TierFor(double width)
    {
        bool labels = width >= LabelBreakpoint;
        return (labels, width >= (labels ? LabelBreakpoint : SearchBreakpoint));
    }

    /// <summary>
    /// Puts the caret in the search box and selects what is there. Ctrl+F lands here. When the bar
    /// is too narrow to draw the field there is nothing to focus, so it says so rather than
    /// silently filtering the list from an invisible box.
    /// </summary>
    public void FocusSearch()
    {
        if (!ShowSearch)
        {
            if (DataContext is ViewModels.CommandBarViewModel bar)
            {
                bar.Explorer.StatusBar.Message = "Search needs a wider window.";
            }

            return;
        }

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        (bool labels, bool search) = TierFor(e.NewSize.Width);
        ShowLabels = labels;
        ShowSearch = search;
    }

    private void OnDensityClick(object sender, RoutedEventArgs e) => Open(DensityButton, alignRight: false);

    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        // The menu runs the shell's commands, and a popup cannot reach the window through the
        // visual tree, so it is handed the shell view model here.
        OverflowMenu.DataContext = Window.GetWindow(this)?.DataContext;
        Open(OverflowButton, alignRight: true);
    }

    private static void Open(Button owner, bool alignRight)
    {
        if (owner.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;

        if (alignRight)
        {
            // The overflow button is the last thing on the bar, so a left-aligned menu hangs off
            // the window edge. Its width is only known once it is up.
            void OnOpened(object? sender, EventArgs e)
            {
                menu.Opened -= OnOpened;
                menu.HorizontalOffset = owner.ActualWidth - menu.ActualWidth;
            }

            menu.Opened += OnOpened;
        }

        menu.IsOpen = true;
    }
}
