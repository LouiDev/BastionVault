using System.Text;
using System.Windows.Input;

namespace Bastion.App.Input;

/// <summary>Where a shortcut is live.</summary>
public enum ShortcutScope
{
    /// <summary>Bound on the shell window; works wherever focus is.</summary>
    Global,

    /// <summary>Bound inside the explorer; only meaningful with a vault open.</summary>
    Explorer,
}

/// <summary>Which section of the Shortcuts dialog a shortcut appears in.</summary>
public enum ShortcutCategory
{
    /// <summary>New / Open / Save / Lock and friends.</summary>
    Vault,

    /// <summary>Import, export, new folder, rename, delete.</summary>
    Content,

    /// <summary>Cut, copy, paste, undo, select all.</summary>
    Editing,

    /// <summary>Back, forward, up, address bar, search.</summary>
    Navigation,

    /// <summary>Focus cycling, context menu, help.</summary>
    Window,
}

/// <summary>One key gesture: a key plus its modifiers.</summary>
/// <param name="Key">The key.</param>
/// <param name="Modifiers">Modifiers that must be held.</param>
public readonly record struct Chord(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    /// <summary>The gesture as the user reads it, for example "Ctrl+Shift+S".</summary>
    public string Display
    {
        get
        {
            var text = new StringBuilder();
            if (Modifiers.HasFlag(ModifierKeys.Control))
            {
                text.Append("Ctrl+");
            }

            if (Modifiers.HasFlag(ModifierKeys.Alt))
            {
                text.Append("Alt+");
            }

            if (Modifiers.HasFlag(ModifierKeys.Shift))
            {
                text.Append("Shift+");
            }

            if (Modifiers.HasFlag(ModifierKeys.Windows))
            {
                text.Append("Win+");
            }

            text.Append(KeyName(Key));
            return text.ToString();
        }
    }

    private static string KeyName(Key key) => key switch
    {
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "?",
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Apps => "Menu",
        Key.Delete => "Delete",
        Key.Space => "Space",
        _ => key.ToString(),
    };
}

/// <summary>One row of the keymap: an action, its scope and every gesture bound to it.</summary>
/// <param name="Id">Stable identifier; also the command name the shell binds.</param>
/// <param name="Description">What the action does, as shown in the Shortcuts dialog.</param>
/// <param name="Category">Section of the Shortcuts dialog.</param>
/// <param name="Scope">Where the shortcut is live.</param>
/// <param name="Chords">Every gesture bound to the action, in the order they are shown.</param>
public sealed record ShortcutEntry(
    string Id,
    string Description,
    ShortcutCategory Category,
    ShortcutScope Scope,
    IReadOnlyList<Chord> Chords)
{
    /// <summary>All gestures joined for display, for example "Alt+Up, Backspace".</summary>
    public string Display => string.Join(", ", Chords.Select(c => c.Display));
}

/// <summary>
/// The single source of the keymap (UI-CONTRACT.md section 3). The shell turns the
/// <see cref="ShortcutScope.Global"/> rows into <c>InputBinding</c>s and the Shortcuts dialog
/// renders all of them; there is no second list anywhere.
/// </summary>
public static class KeyMap
{
    /// <summary>Identifier of the New vault command.</summary>
    public const string NewVault = "NewVault";

    /// <summary>Identifier of the Open vault command.</summary>
    public const string OpenVault = "OpenVault";

    /// <summary>Identifier of the Save command.</summary>
    public const string Save = "Save";

    /// <summary>Identifier of the Save a copy command.</summary>
    public const string SaveCopy = "SaveCopy";

    /// <summary>Identifier of the Lock command.</summary>
    public const string Lock = "Lock";

    /// <summary>Identifier of the Verify command.</summary>
    public const string Verify = "Verify";

    /// <summary>Identifier of the Change credentials command.</summary>
    public const string ChangeCredentials = "ChangeCredentials";

    /// <summary>Identifier of the Settings command.</summary>
    public const string Settings = "Settings";

    /// <summary>Identifier of the Shortcuts command.</summary>
    public const string Shortcuts = "Shortcuts";

    /// <summary>Identifier of the panic command.</summary>
    public const string Panic = "Panic";

    private static readonly ShortcutEntry[] EntriesArray =
    [
        // ── Vault ──────────────────────────────────────────────────────────────
        new(NewVault, "New vault", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.N, ModifierKeys.Control)]),
        new(OpenVault, "Open vault", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.O, ModifierKeys.Control)]),
        new(Save, "Save", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.S, ModifierKeys.Control)]),
        new(SaveCopy, "Save a copy...", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.S, ModifierKeys.Control | ModifierKeys.Shift)]),
        new(Lock, "Lock", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.L, ModifierKeys.Control | ModifierKeys.Shift)]),
        new(Verify, "Verify", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.V, ModifierKeys.Control | ModifierKeys.Shift)]),
        new(ChangeCredentials, "Change password, keyfile or KDF...", ShortcutCategory.Vault, ShortcutScope.Global,
            [new Chord(Key.P, ModifierKeys.Control | ModifierKeys.Shift)]),

        // ── Content ────────────────────────────────────────────────────────────
        new("ImportFiles", "Import files", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.I, ModifierKeys.Control)]),
        new("ImportFolder", "Import folder", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.I, ModifierKeys.Control | ModifierKeys.Shift)]),
        new("Export", "Export selection... (everything when nothing is selected)", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.E, ModifierKeys.Control | ModifierKeys.Shift)]),
        new("NewFolder", "New folder", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.N, ModifierKeys.Control | ModifierKeys.Shift)]),
        new("Rename", "Rename", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.F2)]),
        new("Open", "Open folder or preview file", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.Return)]),
        new("CancelEdit", "Cancel rename, clear search", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.Escape)]),
        new("Delete", "Delete (no confirmation; undoable)", ShortcutCategory.Content, ShortcutScope.Explorer,
            [new Chord(Key.Delete)]),

        // ── Editing ────────────────────────────────────────────────────────────
        new("Undo", "Undo", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.Z, ModifierKeys.Control)]),
        new("Redo", "Redo", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.Y, ModifierKeys.Control)]),
        new("Cut", "Cut (inside the vault)", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.X, ModifierKeys.Control)]),
        new("Copy", "Copy (inside the vault)", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.C, ModifierKeys.Control)]),
        new("Paste", "Paste (Explorer files on the OS clipboard are imported)", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.V, ModifierKeys.Control)]),
        new("CopyPath", "Copy in-vault path", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]),
        new("SelectAll", "Select all", ShortcutCategory.Editing, ShortcutScope.Explorer,
            [new Chord(Key.A, ModifierKeys.Control)]),

        // ── Navigation ─────────────────────────────────────────────────────────
        new("AddressBar", "Focus and edit the address bar", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.L, ModifierKeys.Control), new Chord(Key.D, ModifierKeys.Alt), new Chord(Key.F4)]),
        new("Back", "Back (mouse XButton1)", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.Left, ModifierKeys.Alt)]),
        new("Forward", "Forward (mouse XButton2)", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.Right, ModifierKeys.Alt)]),
        new("Up", "Up one folder", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.Up, ModifierKeys.Alt), new Chord(Key.Back)]),
        new("Root", "Go to the vault root", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.Home, ModifierKeys.Alt)]),
        new("Search", "Focus search", ShortcutCategory.Navigation, ShortcutScope.Explorer,
            [new Chord(Key.F, ModifierKeys.Control), new Chord(Key.E, ModifierKeys.Control)]),

        // ── Window ─────────────────────────────────────────────────────────────
        new("Properties", "Properties", ShortcutCategory.Window, ShortcutScope.Explorer,
            [new Chord(Key.Return, ModifierKeys.Alt)]),
        new("Preview", "Preview the focused item", ShortcutCategory.Window, ShortcutScope.Explorer,
            [new Chord(Key.Space)]),
        new("CycleFocus", "Cycle focus tree → list → address → preview", ShortcutCategory.Window, ShortcutScope.Explorer,
            [new Chord(Key.F6), new Chord(Key.F6, ModifierKeys.Shift)]),
        new("ContextMenu", "Context menu at the focused item", ShortcutCategory.Window, ShortcutScope.Explorer,
            [new Chord(Key.F10, ModifierKeys.Shift), new Chord(Key.Apps)]),
        new(Panic, "Panic: hide the preview and mask names", ShortcutCategory.Window, ShortcutScope.Explorer,
            [new Chord(Key.H, ModifierKeys.Control | ModifierKeys.Shift)]),
        new(Shortcuts, "Keyboard shortcuts", ShortcutCategory.Window, ShortcutScope.Global,
            [new Chord(Key.F1), new Chord(Key.OemQuestion, ModifierKeys.Shift)]),
        new(Settings, "Settings", ShortcutCategory.Window, ShortcutScope.Global,
            [new Chord(Key.OemComma, ModifierKeys.Control)]),
    ];

    /// <summary>Every row of the keymap, in the order the Shortcuts dialog shows them.</summary>
    public static IReadOnlyList<ShortcutEntry> Entries => EntriesArray;

    /// <summary>Looks a row up by its identifier.</summary>
    /// <param name="id">Identifier of the action.</param>
    /// <exception cref="KeyNotFoundException">No row has that identifier.</exception>
    public static ShortcutEntry Get(string id) =>
        EntriesArray.FirstOrDefault(e => e.Id == id)
        ?? throw new KeyNotFoundException($"No shortcut with id '{id}'.");

    /// <summary>The gesture text for an action, for a menu item's shortcut column.</summary>
    /// <param name="id">Identifier of the action.</param>
    public static string GestureText(string id) => Get(id).Display;
}
