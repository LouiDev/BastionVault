using System.Globalization;
using System.Windows.Data;
using BastionVault.App.ViewModels;
using BastionVault.Core;

namespace BastionVault.App.Converters;

/// <summary>
/// Formats a byte count the way every readout in Bastion Vault does. Pass "folder" as the parameter to
/// get the folder treatment - a folder's own row shows a dash rather than a rollup, because a
/// number in the Size column of a folder reads as "this folder is 4 KB" to most people.
/// </summary>
[ValueConversion(typeof(long), typeof(string))]
public sealed class ByteSizeConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly ByteSizeConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is EntryKind.Folder or "folder")
        {
            return "-";
        }

        return value switch
        {
            long bytes => OperationViewModel.FormatBytes(bytes),
            int bytes => OperationViewModel.FormatBytes(bytes),
            EntryItemViewModel item => item.IsFolder ? "-" : OperationViewModel.FormatBytes(item.Length),
            _ => string.Empty,
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
