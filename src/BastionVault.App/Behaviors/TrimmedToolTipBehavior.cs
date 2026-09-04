using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BastionVault.App.Behaviors;

/// <summary>
/// Gives a text block a tooltip only when its text does not fit. A tooltip that repeats what is
/// already legible is noise, so the check happens when the tooltip is about to open rather than on
/// every layout pass, and it cancels the tooltip when the text is fully visible.
/// </summary>
public static class TrimmedToolTipBehavior
{
    /// <summary>Identifies the <c>TrimmedToolTipBehavior.ShowWhenTrimmed</c> attached property.</summary>
    public static readonly DependencyProperty ShowWhenTrimmedProperty = DependencyProperty.RegisterAttached(
        "ShowWhenTrimmed", typeof(bool), typeof(TrimmedToolTipBehavior),
        new PropertyMetadata(false, OnShowWhenTrimmedChanged));

    /// <summary>True when the text block shows a tooltip of its own text once it is trimmed.</summary>
    /// <param name="element">The text block.</param>
    public static bool GetShowWhenTrimmed(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(ShowWhenTrimmedProperty);
    }

    /// <summary>Turns the trimmed-text tooltip on or off.</summary>
    /// <param name="element">The text block.</param>
    /// <param name="value">True to show a tooltip when the text is trimmed.</param>
    public static void SetShowWhenTrimmed(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ShowWhenTrimmedProperty, value);
    }

    /// <summary>True when the rendered text is wider than the space the block has.</summary>
    /// <param name="text">The text block to measure.</param>
    public static bool IsTrimmed(TextBlock text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (string.IsNullOrEmpty(text.Text) || text.ActualWidth <= 0)
        {
            return false;
        }

        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var measured = new FormattedText(
            text.Text,
            CultureInfo.CurrentUICulture,
            text.FlowDirection,
            typeface,
            text.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(text).PixelsPerDip);

        double available = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        return measured.Width > available + 0.5;
    }

    private static void OnShowWhenTrimmedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        text.ToolTipOpening -= OnToolTipOpening;

        if (e.NewValue is not true)
        {
            return;
        }

        // A tooltip has to exist for ToolTipOpening to fire at all; the handler cancels it when
        // the text fits, and refreshes it from the current text when it does not.
        text.ToolTip ??= text.Text;
        text.ToolTipOpening += OnToolTipOpening;
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        if (!IsTrimmed(text))
        {
            e.Handled = true;
            return;
        }

        text.ToolTip = text.Text;
    }
}
