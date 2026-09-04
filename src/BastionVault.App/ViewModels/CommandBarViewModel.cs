using System.Windows.Input;
using BastionVault.App.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BastionVault.App.ViewModels;

/// <summary>One button of the command bar.</summary>
/// <param name="Id">Keymap identifier, also the automation id.</param>
/// <param name="Label">Text under or beside the glyph.</param>
/// <param name="GlyphKey">Resource key of the 20 px glyph.</param>
/// <param name="ToolTip">"Name (Shortcut)", built from the keymap.</param>
/// <param name="Command">What the button runs.</param>
/// <param name="ShowLabel">True for the two or three buttons that carry their label in the bar.</param>
public sealed record CommandBarButton(
    string Id,
    string Label,
    string GlyphKey,
    string ToolTip,
    ICommand Command,
    bool ShowLabel = false);

/// <summary>A run of buttons; the bar draws a vertical seam between groups.</summary>
/// <param name="Buttons">The buttons in the group, left to right.</param>
public sealed record CommandBarGroup(IReadOnlyList<CommandBarButton> Buttons);

/// <summary>
/// The explorer's command bar. Every tooltip reads "Name (Shortcut)" and the shortcut comes from
/// <see cref="KeyMap"/>, the same table that binds the keys and fills the Shortcuts dialog, so a
/// tooltip cannot promise a gesture that is not bound.
/// </summary>
public sealed partial class CommandBarViewModel : ObservableObject
{
    /// <summary>Builds the bar for an explorer.</summary>
    /// <param name="explorer">The explorer whose commands the buttons run.</param>
    public CommandBarViewModel(ExplorerViewModel explorer)
    {
        ArgumentNullException.ThrowIfNull(explorer);

        Explorer = explorer;

        Groups =
        [
            new CommandBarGroup(
            [
                Button("NewFolder", "New folder", "Glyph.NewFolder", explorer.NewFolderCommand, showLabel: true),
                Button("ImportFiles", "Import files", "Glyph.ImportFiles", explorer.ImportFilesCommand, showLabel: true),
                Button("ImportFolder", "Import folder", "Glyph.ImportFolder", explorer.ImportFolderCommand, showLabel: true),
                Button("Export", "Export", "Glyph.Export", explorer.ExportCommand, showLabel: true),
            ]),
            new CommandBarGroup(
            [
                Button("Cut", "Cut", "Glyph.Cut", explorer.CutCommand),
                Button("Copy", "Copy", "Glyph.Copy", explorer.CopyCommand),
                Button("Paste", "Paste", "Glyph.Paste", explorer.PasteCommand),
                Button("Rename", "Rename", "Glyph.Rename", explorer.RenameCommand),
                Button("Delete", "Delete", "Glyph.Delete", explorer.DeleteCommand),
            ]),
            new CommandBarGroup(
            [
                Button("Undo", "Undo", "Glyph.Undo", explorer.UndoCommand),
                Button("Redo", "Redo", "Glyph.Redo", explorer.RedoCommand),
            ]),
        ];
    }

    /// <summary>The explorer the bar drives.</summary>
    public ExplorerViewModel Explorer { get; }

    /// <summary>The groups, left to right.</summary>
    public IReadOnlyList<CommandBarGroup> Groups { get; }

    /// <summary>Tooltip for the preview toggle, which is a toggle rather than a plain button.</summary>
    public string PreviewToolTip => Tip("Preview pane", "Preview");

    /// <summary>Tooltip for the properties button.</summary>
    public string PropertiesToolTip => Tip("Properties", "Properties");

    /// <summary>Tooltip for the search box.</summary>
    public string SearchToolTip => Tip("Search", "Search");

    /// <summary>
    /// "Name (Shortcut)", or just the name when the keymap has no gesture for the action.
    /// </summary>
    /// <param name="label">What the action is called.</param>
    /// <param name="keyMapId">Identifier of the keymap row.</param>
    public static string Tip(string label, string keyMapId)
    {
        try
        {
            ShortcutEntry entry = KeyMap.Get(keyMapId);
            return entry.Chords.Count == 0 ? label : $"{label} ({entry.Chords[0].Display})";
        }
        catch (KeyNotFoundException)
        {
            return label;
        }
    }

    private static CommandBarButton Button(string id, string label, string glyph, ICommand command, bool showLabel = false) =>
        new(id, label, glyph, Tip(label, id), command, showLabel);
}
