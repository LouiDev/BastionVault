using System.Globalization;
using System.Windows.Data;

namespace Bastion.App.Converters;

/// <summary>
/// Turns a timestamp into the short, human line the Modified column shows: the time for today,
/// "Yesterday" plus the time for yesterday, a weekday within the last week, and a plain short date
/// after that. Anything older than a year keeps the year.
/// </summary>
[ValueConversion(typeof(DateTimeOffset), typeof(string))]
public sealed class RelativeDateConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly RelativeDateConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTimeOffset? moment = value switch
        {
            DateTimeOffset offset => offset,
            DateTime plain => new DateTimeOffset(plain),
            _ => null,
        };

        return moment is null ? string.Empty : Format(moment.Value, DateTimeOffset.Now, culture);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Formats a timestamp relative to a reference moment.</summary>
    /// <param name="moment">The timestamp, in any offset.</param>
    /// <param name="now">What counts as "now"; a test passes a fixed value.</param>
    /// <param name="culture">Culture for the date and time parts.</param>
    public static string Format(DateTimeOffset moment, DateTimeOffset now, CultureInfo? culture = null)
    {
        CultureInfo formats = culture ?? CultureInfo.CurrentCulture;
        DateTime local = moment.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        int days = (today - local.Date).Days;

        if (days == 0)
        {
            return local.ToString("t", formats);
        }

        if (days == 1)
        {
            return "Yesterday " + local.ToString("t", formats);
        }

        if (days is > 1 and < 7)
        {
            // "t" throughout, so the column never mixes a 12-hour "Yesterday 7:22 AM" with a
            // hard-coded 24-hour "Sat 03:22" two rows apart.
            return local.ToString("ddd ", formats) + local.ToString("t", formats);
        }

        if (days < 0)
        {
            // A clock skew or a file from the future; do not pretend it is recent.
            return local.ToString("g", formats);
        }

        return local.Year == today.Year
            ? local.ToString("d MMM ", formats) + local.ToString("t", formats)
            : local.ToString("d MMM yyyy", formats);
    }
}
