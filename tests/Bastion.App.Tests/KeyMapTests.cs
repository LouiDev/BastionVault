using Bastion.App.Input;

namespace Bastion.App.Tests;

/// <summary>
/// The keymap is the single source for both the input bindings and the Shortcuts dialog, so the
/// only thing that can go wrong is the map contradicting itself.
/// </summary>
public sealed class KeyMapTests
{
    [Fact]
    public void NoGestureIsBoundTwice()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ShortcutEntry entry in KeyMap.Entries)
        {
            foreach (Chord chord in entry.Chords)
            {
                string gesture = chord.Display;
                Assert.False(
                    seen.ContainsKey(gesture),
                    $"{gesture} is bound to both '{seen.GetValueOrDefault(gesture)}' and '{entry.Id}'.");
                seen[gesture] = entry.Id;
            }
        }
    }

    [Fact]
    public void EveryIdIsUnique()
    {
        string[] ids = [.. KeyMap.Entries.Select(e => e.Id)];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryEntryHasAGestureAndADescription()
    {
        foreach (ShortcutEntry entry in KeyMap.Entries)
        {
            Assert.NotEmpty(entry.Chords);
            Assert.False(string.IsNullOrWhiteSpace(entry.Description), entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(entry.Display), entry.Id);
        }
    }

    [Fact]
    public void TheContractShortcutsAreThere()
    {
        Assert.Equal("Ctrl+N", KeyMap.GestureText(KeyMap.NewVault));
        Assert.Equal("Ctrl+O", KeyMap.GestureText(KeyMap.OpenVault));
        Assert.Equal("Ctrl+S", KeyMap.GestureText(KeyMap.Save));
        Assert.Equal("Ctrl+Shift+S", KeyMap.GestureText(KeyMap.SaveCopy));
        Assert.Equal("Ctrl+Shift+L", KeyMap.GestureText(KeyMap.Lock));
        Assert.Equal("Ctrl+Shift+V", KeyMap.GestureText(KeyMap.Verify));
        Assert.Equal("Ctrl+,", KeyMap.GestureText(KeyMap.Settings));
        Assert.Equal("Ctrl+Shift+H", KeyMap.GestureText(KeyMap.Panic));
        Assert.Equal("Ctrl+Shift+P", KeyMap.GestureText(KeyMap.ChangeCredentials));
    }

    [Fact]
    public void MultiGestureRowsAreJoinedForDisplay()
    {
        Assert.Equal("Ctrl+L, Alt+D, F4", KeyMap.Get("AddressBar").Display);
        Assert.Equal("Alt+Up, Backspace", KeyMap.Get("Up").Display);
    }

    [Fact]
    public void EveryGlobalRowIsSomethingTheShellCanBind()
    {
        // The shell maps ids to commands by hand; a global row with no command would be a silent
        // dead key, so the set is asserted here.
        string[] global = [.. KeyMap.Entries.Where(e => e.Scope == ShortcutScope.Global).Select(e => e.Id)];

        Assert.Equal(
            [
                KeyMap.NewVault, KeyMap.OpenVault, KeyMap.Save, KeyMap.SaveCopy, KeyMap.Lock,
                KeyMap.Verify, KeyMap.ChangeCredentials, KeyMap.Shortcuts, KeyMap.Settings,
            ],
            global);
    }

    [Fact]
    public void UnknownIdsThrow() => Assert.Throws<KeyNotFoundException>(() => KeyMap.Get("NoSuchThing"));
}
