using System.Windows.Input;
using BastionVault.App.Input;
using BastionVault.App.Views;

namespace BastionVault.App.Tests;

/// <summary>
/// Which keymap gestures a focused text box keeps for itself. The explorer used to hand a text
/// editor everything that was not an Alt gesture or F6, which killed Import folder, Export, New
/// folder, Copy path, Panic, the address bar and search the moment the caret sat in the search
/// box - found by driving the built app.
/// </summary>
public sealed class TextEditorKeysTests
{
    /// <summary>The editing gestures: a text box must keep these.</summary>
    /// <param name="key">Key of the chord.</param>
    /// <param name="modifiers">Modifiers of the chord.</param>
    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None)]              // cancels an inline rename
    [InlineData(Key.Return, ModifierKeys.None)]              // commits it
    [InlineData(Key.F2, ModifierKeys.None)]                  // must not restart it
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.Space, ModifierKeys.None)]
    [InlineData(Key.Back, ModifierKeys.None)]
    [InlineData(Key.F10, ModifierKeys.Shift)]                // the text box's own context menu
    [InlineData(Key.Apps, ModifierKeys.None)]
    [InlineData(Key.Z, ModifierKeys.Control)]
    [InlineData(Key.Y, ModifierKeys.Control)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    public void A_text_editor_keeps_the_editing_gestures(Key key, ModifierKeys modifiers) =>
        Assert.True(ExplorerView.BelongsToTextEditor(new Chord(key, modifiers)));

    /// <summary>Everything else means nothing inside a text box and must reach the explorer.</summary>
    /// <param name="key">Key of the chord.</param>
    /// <param name="modifiers">Modifiers of the chord.</param>
    [Theory]
    [InlineData(Key.I, ModifierKeys.Control)]                                   // import files
    [InlineData(Key.I, ModifierKeys.Control | ModifierKeys.Shift)]              // import folder
    [InlineData(Key.E, ModifierKeys.Control | ModifierKeys.Shift)]              // export
    [InlineData(Key.N, ModifierKeys.Control | ModifierKeys.Shift)]              // new folder
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]              // copy path
    [InlineData(Key.H, ModifierKeys.Control | ModifierKeys.Shift)]              // panic
    [InlineData(Key.L, ModifierKeys.Control)]                                   // address bar
    [InlineData(Key.F, ModifierKeys.Control)]                                   // search
    [InlineData(Key.E, ModifierKeys.Control)]                                   // search
    [InlineData(Key.F4, ModifierKeys.None)]                                     // address bar
    [InlineData(Key.F6, ModifierKeys.None)]                                     // cycle focus
    [InlineData(Key.D, ModifierKeys.Alt)]
    [InlineData(Key.Left, ModifierKeys.Alt)]
    [InlineData(Key.Up, ModifierKeys.Alt)]
    [InlineData(Key.Home, ModifierKeys.Alt)]
    [InlineData(Key.Return, ModifierKeys.Alt)]                                  // properties
    public void Every_other_gesture_reaches_the_explorer(Key key, ModifierKeys modifiers) =>
        Assert.False(ExplorerView.BelongsToTextEditor(new Chord(key, modifiers)));

    /// <summary>
    /// The two rules together must cover every Explorer-scope chord in the keymap, so a new row
    /// cannot quietly become a key that does nothing while the search box has focus.
    /// </summary>
    [Fact]
    public void Every_explorer_chord_is_classified_one_way_or_the_other()
    {
        IEnumerable<Chord> chords = KeyMap.Entries
            .Where(e => e.Scope == ShortcutScope.Explorer)
            .SelectMany(e => e.Chords);

        Assert.NotEmpty(chords);

        // Bare and Shift-only chords belong to the editor unless they are function keys the
        // explorer owns; Control chords belong to it only when they are the clipboard six.
        foreach (Chord chord in chords)
        {
            bool owned = ExplorerView.BelongsToTextEditor(chord);

            if (chord.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                Assert.False(owned, $"{chord.Modifiers}+{chord.Key} carries Alt and must pass through");
            }
        }
    }
}
