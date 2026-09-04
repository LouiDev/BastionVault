using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BastionVault.App.Views;

/// <summary>
/// The blueprint empty state: line art, a headline and one sentence. The explorer shows it for an
/// empty folder and for a search that found nothing (UI-CONTRACT.md section 7).
/// </summary>
public partial class EmptyStateView : UserControl
{
    /// <summary>Identifies the <see cref="Blueprint"/> dependency property.</summary>
    public static readonly DependencyProperty BlueprintProperty = DependencyProperty.Register(
        nameof(Blueprint), typeof(Geometry), typeof(EmptyStateView), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Headline"/> dependency property.</summary>
    public static readonly DependencyProperty HeadlineProperty = DependencyProperty.Register(
        nameof(Headline), typeof(string), typeof(EmptyStateView), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Body"/> dependency property.</summary>
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(string), typeof(EmptyStateView), new PropertyMetadata(string.Empty));

    /// <summary>Creates the empty state.</summary>
    public EmptyStateView() => InitializeComponent();

    /// <summary>The line art, one of the <c>Blueprint.*</c> geometries.</summary>
    public Geometry? Blueprint
    {
        get => (Geometry?)GetValue(BlueprintProperty);
        set => SetValue(BlueprintProperty, value);
    }

    /// <summary>The headline, set in the title face.</summary>
    public string Headline
    {
        get => (string)GetValue(HeadlineProperty);
        set => SetValue(HeadlineProperty, value);
    }

    /// <summary>One sentence saying what to do next.</summary>
    public string Body
    {
        get => (string)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }
}
