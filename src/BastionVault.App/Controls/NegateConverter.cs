using System.Globalization;
using System.Windows.Data;

namespace BastionVault.App.Controls;

/// <summary>
/// Returns the arithmetic negation of a <see cref="double"/>. Used to slide the grid-view header
/// row against the horizontal scroll offset so the header and the cells stay aligned.
/// </summary>
[ValueConversion(typeof(double), typeof(double))]
public sealed class NegateConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly NegateConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? -d : 0d;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? -d : 0d;
}
