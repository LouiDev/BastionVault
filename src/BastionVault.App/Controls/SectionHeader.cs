using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace BastionVault.App.Controls;

/// <summary>
/// A rule-and-caps section header (UI-CONTRACT.md section 2, signature detail 6): the label in
/// uppercase caption type with +8 % tracking, followed by a hairline that fills the row.
/// WPF has no letter-spacing, so the tracking is produced by inserting hair spaces (U+200A,
/// about one tenth of an em) between the characters of the label.
/// </summary>
public sealed class SectionHeader : Control
{
    private const char HairSpace = '\u200A';

    /// <summary>Identifies the <see cref="Text"/> dependency property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SectionHeader),
        new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(SectionHeader), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the read-only <see cref="DisplayText"/> dependency property.</summary>
    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    /// <summary>The label, written normally; it is uppercased and tracked for display.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The uppercased, hair-spaced form of <see cref="Text"/> that the template renders.</summary>
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    /// <summary>Uppercases <paramref name="text"/> and inserts a hair space between characters.</summary>
    /// <param name="text">Label to transform; <see langword="null"/> yields an empty string.</param>
    public static string Track(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string upper = text.ToUpper(CultureInfo.CurrentCulture);
        var builder = new StringBuilder(upper.Length * 2);
        for (int i = 0; i < upper.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(HairSpace);
            }

            builder.Append(upper[i]);
        }

        return builder.ToString();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SectionHeader)d).SetValue(DisplayTextPropertyKey, Track(e.NewValue as string));
}
