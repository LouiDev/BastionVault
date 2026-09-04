using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Bastion.App.Controls;
using Bastion.App.Services;
using Bastion.App.ViewModels;
using Bastion.Core;

namespace Bastion.App.Converters;

/// <summary>
/// Resolves a <c>Glyph.*</c> resource key to the character it stands for. View models name a
/// glyph by key so they never hold a private-use code point, and this is the one place that
/// turns the key into text.
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class GlyphKeyConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly GlyphKeyConverter Instance = new();

    /// <summary>Looks a glyph key up in the application resources.</summary>
    /// <param name="key">Resource key, for example "Glyph.Folder".</param>
    /// <returns>The glyph, or an empty string when the key is unknown or there is no application.</returns>
    public static string Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || Application.Current is null)
        {
            return string.Empty;
        }

        return Application.Current.TryFindResource(key) as string ?? string.Empty;
    }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Resolve(value as string);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Folder or file, as a glyph. The tree uses it with the "open" parameter so an expanded folder
/// gets the open-folder icon.
/// </summary>
[ValueConversion(typeof(EntryKind), typeof(string))]
public sealed class EntryKindToGlyphConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly EntryKindToGlyphConverter Instance = new();

    /// <summary>The glyph key for a kind.</summary>
    /// <param name="kind">Folder or file.</param>
    /// <param name="isOpen">True for an expanded folder.</param>
    public static string KeyFor(EntryKind kind, bool isOpen) => kind switch
    {
        EntryKind.Folder => isOpen ? "Glyph.FolderOpen" : "Glyph.Folder",
        _ => "Glyph.File",
    };

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // A bound bool is a tree node's IsExpanded: the node is a folder either way.
        if (value is bool expanded)
        {
            return GlyphKeyConverter.Resolve(KeyFor(EntryKind.Folder, expanded));
        }

        bool isOpen = parameter is true or "open";
        EntryKind kind = value switch
        {
            EntryKind typed => typed,
            EntryItemViewModel item => item.Kind,
            _ => EntryKind.File,
        };

        return GlyphKeyConverter.Resolve(KeyFor(kind, isOpen));
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The type icon of a row, from <see cref="FileTypeCatalog"/>. It never asks the Windows shell,
/// so an in-vault name is never handed to an out-of-process icon handler.
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class FileTypeToGlyphConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly FileTypeToGlyphConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            EntryItemViewModel item => item.GlyphKey,
            EntryInfo info => FileTypeCatalog.Describe(info.Kind, info.Name).GlyphKey,
            string name => FileTypeCatalog.Describe(name).GlyphKey,
            _ => "Glyph.File",
        };

        return GlyphKeyConverter.Resolve(key);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The friendly type name of a row, from <see cref="FileTypeCatalog"/>.</summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class FileTypeToNameConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly FileTypeToNameConverter Instance = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EntryItemViewModel item => item.TypeName,
        EntryInfo info => FileTypeCatalog.Describe(info.Kind, info.Name).FriendlyType,
        string name => FileTypeCatalog.Describe(name).FriendlyType,
        _ => string.Empty,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps an entry's save state to the pip the status rail draws: a filled dot for something new, a
/// ring for something edited, nothing for something already stored
/// (UI-CONTRACT.md section 2, signature detail 2).
/// </summary>
[ValueConversion(typeof(EntryState), typeof(PipState))]
public sealed class StateToPipConverter : IValueConverter
{
    /// <summary>The shared instance; the converter is stateless.</summary>
    public static readonly StateToPipConverter Instance = new();

    /// <summary>The pip for a save state.</summary>
    /// <param name="state">Stored, Added or Changed.</param>
    public static PipState PipFor(EntryState state) => state switch
    {
        EntryState.Added => PipState.Added,
        EntryState.Changed => PipState.Changed,
        _ => PipState.None,
    };

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EntryState state => PipFor(state),
        EntryItemViewModel item => PipFor(item.State),
        _ => PipState.None,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
