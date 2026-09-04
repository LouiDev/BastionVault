using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BastionVault.App.Converters;

/// <summary>
/// True when the bound value equals the parameter, which may be a comma-separated list of names.
/// It is how a single enum drives a row of toggles - density, search scope - without one bool per
/// state on the view model.
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly EnumMatchConverter Instance = new();

    /// <summary>True when <paramref name="value"/> matches one of the names in <paramref name="parameter"/>.</summary>
    /// <param name="value">The bound value.</param>
    /// <param name="parameter">One name, or several separated by commas.</param>
    public static bool Matches(object? value, object? parameter)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        string actual = value.ToString() ?? string.Empty;

        foreach (string wanted in (parameter.ToString() ?? string.Empty).Split(','))
        {
            if (string.Equals(actual, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Matches(value, parameter);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>Visible when the bound value matches the parameter; collapsed otherwise.</summary>
public sealed class EnumMatchToVisibilityConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly EnumMatchToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        EnumMatchConverter.Matches(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element while every bound boolean is false, and shows it as soon as one is true.
/// The empty states use it: "show the blueprint only when the list is empty and nothing is
/// loading".
/// </summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly AllTrueToVisibilityConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (object? value in values)
        {
            if (value is not true)
            {
                return Visibility.Collapsed;
            }
        }

        return values.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
