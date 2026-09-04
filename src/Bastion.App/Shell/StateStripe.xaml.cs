using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using Bastion.App.ViewModels;

namespace Bastion.App.Shell;

/// <summary>
/// The 2 px state bar under the title bar (UI-CONTRACT.md section 2, signature detail 4). It is
/// the one always-visible answer to "what is this window doing right now": nothing, locked,
/// unlocked and saved, unsaved, working, or broken.
/// </summary>
public partial class StateStripe : UserControl
{
    /// <summary>Identifies the <see cref="State"/> dependency property.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StripeState), typeof(StateStripe),
        new PropertyMetadata(StripeState.None, OnStateChanged));

    private Storyboard? _shimmer;

    /// <summary>Creates the stripe.</summary>
    public StateStripe()
    {
        InitializeComponent();
        Loaded += (_, _) => Apply();
    }

    /// <summary>What the stripe is showing.</summary>
    public StripeState State
    {
        get => (StripeState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StateStripe)d).Apply();

    private void Apply()
    {
        if (!IsLoaded)
        {
            return;
        }

        StopShimmer();
        Solid.Visibility = Visibility.Visible;
        Dashed.Visibility = Visibility.Collapsed;
        Shimmer.Visibility = Visibility.Collapsed;

        switch (State)
        {
            case StripeState.None:
                Solid.Fill = Brushes.Transparent;
                break;

            case StripeState.Locked:
                Solid.SetResourceReference(Shape.FillProperty, "Brush.StrokeControl");
                break;

            case StripeState.Saved:
                Solid.SetResourceReference(Shape.FillProperty, "Brush.AccentDim");
                break;

            case StripeState.Unsaved:
                Solid.Fill = Brushes.Transparent;
                Dashed.Visibility = Visibility.Visible;
                break;

            case StripeState.Running:
                Solid.SetResourceReference(Shape.FillProperty, "Brush.AccentDim");
                Shimmer.Visibility = Visibility.Visible;
                StartShimmer();
                break;

            case StripeState.IntegrityFailure:
                Solid.SetResourceReference(Shape.FillProperty, "Brush.DangerText");
                break;

            default:
                Solid.Fill = Brushes.Transparent;
                break;
        }
    }

    private void StartShimmer()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        _shimmer = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddSweep(_shimmer, S0, -0.40, 1.00);
        AddSweep(_shimmer, S1, -0.20, 1.20);
        AddSweep(_shimmer, S2, 0.00, 1.40);
        _shimmer.Begin();
    }

    private static void AddSweep(Storyboard storyboard, GradientStop stop, double from, double to)
    {
        var animation = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(1400)));
        Storyboard.SetTarget(animation, stop);
        Storyboard.SetTargetProperty(animation, new PropertyPath(GradientStop.OffsetProperty));
        storyboard.Children.Add(animation);
    }

    private void StopShimmer()
    {
        _shimmer?.Stop();
        _shimmer = null;
    }
}
