using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bastion.App.Controls;

/// <summary>Collapses when the bound value is <see langword="true"/>.</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}

/// <summary>Shows the element only when the bound string has content.</summary>
[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows the element only when the bound value is not <see langword="null"/>.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Multiplies a width by a 0..1 fraction. Used by the password-strength meter, which is a plain
/// rectangle rather than a ProgressBar so it can carry its own colour ramp.
/// </summary>
public sealed class FractionOfWidthConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length < 2 || values[0] is not double width || values[1] is not double fraction)
        {
            return 0d;
        }

        return Math.Max(0, width * Math.Clamp(fraction, 0, 1));
    }

    /// <inheritdoc />
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
