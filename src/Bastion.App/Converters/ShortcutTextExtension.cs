using System.Windows.Markup;
using Bastion.App.Input;

namespace Bastion.App.Converters;

/// <summary>
/// A markup extension that writes "Name (Ctrl+S)" from <see cref="KeyMap"/>, so a tooltip or a
/// menu item cannot promise a gesture that nothing binds. With no <see cref="Name"/> it yields the
/// gesture alone, which is what a menu item's shortcut column wants.
/// </summary>
/// <remarks>Usage: <c>ToolTip="{c:ShortcutText Id=NewFolder, Name=New folder}"</c>.</remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class ShortcutTextExtension : MarkupExtension
{
    /// <summary>Creates an empty extension; set <see cref="Id"/> in markup.</summary>
    public ShortcutTextExtension()
    {
    }

    /// <summary>Creates the extension for a keymap row.</summary>
    /// <param name="id">Identifier of the keymap row.</param>
    public ShortcutTextExtension(string id) => Id = id;

    /// <summary>Identifier of the keymap row, for example "NewFolder".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the action is called; omit it to get the gesture on its own.</summary>
    public string? Name { get; set; }

    /// <summary>The gesture text of a keymap row, or an empty string when the row has none.</summary>
    /// <param name="id">Identifier of the keymap row.</param>
    public static string Gesture(string id)
    {
        try
        {
            ShortcutEntry entry = KeyMap.Get(id);
            return entry.Chords.Count == 0 ? string.Empty : entry.Chords[0].Display;
        }
        catch (KeyNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string gesture = Gesture(Id);

        if (string.IsNullOrEmpty(Name))
        {
            return gesture;
        }

        return gesture.Length == 0 ? Name : $"{Name} ({gesture})";
    }
}
