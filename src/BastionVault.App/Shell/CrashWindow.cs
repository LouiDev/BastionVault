using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BastionVault.App.Shell;

/// <summary>
/// The window the crash handler shows after it has logged the failure and zeroed the keys. It is built in
/// code with literal colours and en-US button labels: the native <c>MessageBox</c> it replaces put
/// "OK / Abbrechen" under English text on a German Windows, and the rest of the UI is pinned to en-US.
/// No bindings, no theme dictionaries and no styles are used, because the dispatcher that runs it has just
/// thrown; everything it needs is in this file. The message never contains vault content or names.
/// </summary>
internal sealed class CrashWindow : Window
{
    private static readonly Color Bg0 = Color.FromRgb(0x0E, 0x11, 0x16);
    private static readonly Color Bg1 = Color.FromRgb(0x15, 0x19, 0x22);
    private static readonly Color Divider = Color.FromRgb(0x2A, 0x32, 0x41);
    private static readonly Color TextPrimary = Color.FromRgb(0xE7, 0xEA, 0xF0);
    private static readonly Color TextSecondary = Color.FromRgb(0x9A, 0xA3, 0xB2);
    private static readonly Color TextMono = Color.FromRgb(0xC9, 0xD1, 0xDE);
    private static readonly Color Accent = Color.FromRgb(0xF2, 0xA9, 0x3B);
    private static readonly Color OnAccent = Color.FromRgb(0x1A, 0x14, 0x08);
    private static readonly Color Danger = Color.FromRgb(0xFF, 0x6B, 0x6F);
    private static readonly Color ControlStroke = Color.FromRgb(0x62, 0x6E, 0x86);

    private CrashWindow(string detail)
    {
        Title = "Bastion Vault";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Background = new SolidColorBrush(Bg0);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Foreground = new SolidColorBrush(TextPrimary);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        root.Children.Add(new TextBlock
        {
            Text = "Bastion Vault hit an unexpected error",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Danger),
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Text = "The vault keys have been zeroed. Continue only to save your work somewhere safe; then restart Bastion Vault.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(TextPrimary),
        });

        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Bg1),
            BorderBrush = new SolidColorBrush(Divider),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = detail,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(TextMono),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        root.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Text = "The details are in the log under %LOCALAPPDATA%\\BastionVault\\logs.",
            Foreground = new SolidColorBrush(TextSecondary),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };

        Button exit = MakeButton("Exit", primary: false);
        exit.IsCancel = true;
        exit.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        Button proceed = MakeButton("Continue", primary: true);
        proceed.IsDefault = true;
        proceed.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        buttons.Children.Add(exit);
        buttons.Children.Add(proceed);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>
    /// Shows the crash message and asks whether to keep the process alive. Falls back to the native message
    /// box when even this window cannot be shown; the caller treats any failure as "exit".
    /// </summary>
    /// <param name="detail">Exception type and message; no vault content.</param>
    /// <param name="owner">The main window, when it is still usable.</param>
    /// <returns>True when the user chose to continue.</returns>
    public static bool AskToContinue(string detail, Window? owner)
    {
        var window = new CrashWindow(detail);
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return window.ShowDialog() == true;
    }

    private static Button MakeButton(string label, bool primary)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 96,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Background = new SolidColorBrush(primary ? Accent : Bg1),
            Foreground = new SolidColorBrush(primary ? OnAccent : TextPrimary),
            BorderBrush = new SolidColorBrush(primary ? Accent : ControlStroke),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        // The default WPF button template repaints on hover with the system colours, which would put a
        // light grey block into a dark window; a flat template keeps the literal colours above.
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        button.Template = template;

        return button;
    }
}
