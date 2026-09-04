using System.ComponentModel;
using System.Windows;

namespace Bastion.App.Services;

/// <summary>
/// Keeps the High Contrast dictionary in step with the OS and with the theme setting
/// (UI-CONTRACT.md section 1.14). Every brush in the theme is a <c>DynamicResource</c>, so merging or
/// un-merging <c>Themes/HighContrast.xaml</c> is enough to re-paint a running window - the user does not
/// have to restart Bastion after turning High Contrast on in Windows.
/// </summary>
public sealed class ThemeController : IDisposable
{
    private static readonly Uri HighContrastSource = new("Themes/HighContrast.xaml", UriKind.Relative);

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILog _log;
    private ResourceDictionary? _highContrast;
    private bool _disposed;

    /// <summary>Creates the controller and applies the current state once.</summary>
    /// <param name="settings">Where the theme preference lives.</param>
    /// <param name="dispatcher">UI thread marshaller; the OS event arrives on any thread.</param>
    /// <param name="log">Log for the rare case where the dictionary cannot be loaded.</param>
    public ThemeController(ISettingsService settings, IUiDispatcher dispatcher, ILog log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _dispatcher = dispatcher;
        _log = log;

        _settings.Changed += OnSettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>True when the High Contrast map is currently merged.</summary>
    public bool IsHighContrastApplied => _highContrast is not null;

    /// <summary>Merges or un-merges the High Contrast map to match the OS and the setting.</summary>
    public void Apply()
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        bool wanted = _settings.Current.Theme == AppTheme.HighContrastAuto && SystemParameters.HighContrast;
        if (wanted == IsHighContrastApplied)
        {
            return;
        }

        try
        {
            if (wanted)
            {
                var dictionary = new ResourceDictionary { Source = HighContrastSource };
                application.Resources.MergedDictionaries.Add(dictionary);
                _highContrast = dictionary;
                _log.Info("High contrast theme applied.");
            }
            else
            {
                application.Resources.MergedDictionaries.Remove(_highContrast!);
                _highContrast = null;
                _log.Info("High contrast theme removed.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException)
        {
            // A theme that will not load must not take the window down with it.
            _log.Warn("Could not switch the high contrast theme.", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Post();

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e is null || e.PropertyName is null or nameof(SystemParameters.HighContrast))
        {
            Post();
        }
    }

    private void Post()
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            _dispatcher.Post(Apply);
        }
    }
}
