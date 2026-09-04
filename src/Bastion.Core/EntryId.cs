namespace Bastion.Core;

/// <summary>
/// Stable identity of an entry inside a vault. Ids are assigned once and never reused:
/// a save never renumbers, so the App may cache ids across saves.
/// </summary>
/// <param name="Value">Raw numeric id; <c>0</c> denotes the (implicit) root folder.</param>
public readonly record struct EntryId(uint Value)
{
    /// <summary>The implicit root folder of every vault (id <c>0</c>). It has no <see cref="EntryInfo"/>.</summary>
    public static readonly EntryId Root = new(0);

    /// <summary>True when this id denotes the vault root.</summary>
    public bool IsRoot => Value == 0;
}
