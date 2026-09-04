using BastionVault.App.Services;
using BastionVault.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BastionVault.App.ViewModels;

/// <summary>
/// One row of the entry list: a thin, immutable-ish face over an <see cref="EntryInfo"/> snapshot
/// plus the two things the row needs that the snapshot does not carry - whether the entry is on
/// the internal clipboard as a cut, and whether names are currently masked. Rows are cheap because
/// a folder with ten thousand files builds ten thousand of them.
/// </summary>
public sealed partial class EntryItemViewModel : ObservableObject, ISortableEntry
{
    private readonly FileTypeInfo _type;

    [ObservableProperty]
    private EntryInfo _info;

    [ObservableProperty]
    private bool _isCut;

    [ObservableProperty]
    private bool _isMasked;

    /// <summary>Creates a row over a snapshot.</summary>
    /// <param name="info">The entry snapshot from Core.</param>
    /// <param name="pathInVault">In-vault path, used by search results and tooltips.</param>
    public EntryItemViewModel(EntryInfo info, string pathInVault)
    {
        ArgumentNullException.ThrowIfNull(info);

        _info = info;
        Path = pathInVault;
        _type = FileTypeCatalog.Describe(info.Kind, info.Name);
    }

    /// <summary>Stable identifier of the entry.</summary>
    public EntryId Id => Info.Id;

    /// <summary>Folder or file.</summary>
    public EntryKind Kind => Info.Kind;

    /// <summary>The entry name, or a mask of the same length while panic mode is on.</summary>
    public string Name => IsMasked ? Mask(Info.Name) : Info.Name;

    /// <summary>The real name, whatever the mask says. Used for rename and sorting.</summary>
    public string RealName => Info.Name;

    /// <summary>In-vault path of the entry.</summary>
    public string Path { get; }

    /// <summary>Plaintext bytes; a folder reports its recursive rollup.</summary>
    public long Length => Info.Length;

    /// <summary>Number of direct children; zero for a file.</summary>
    public int ChildCount => Info.ChildCount;

    /// <summary>Last modification time.</summary>
    public DateTimeOffset ModifiedUtc => Info.ModifiedUtc;

    /// <summary>Creation time.</summary>
    public DateTimeOffset CreatedUtc => Info.CreatedUtc;

    /// <summary>Whether the entry is unchanged, new or edited since the last save.</summary>
    public EntryState State => Info.State;

    /// <summary>Friendly type name for the Type column.</summary>
    public string TypeName => _type.FriendlyType;

    /// <summary>Resource key of the 16 px type icon.</summary>
    public string GlyphKey => _type.GlyphKey;

    /// <summary>How the preview pane should try to render this entry.</summary>
    public PreviewKind Preview => _type.Preview;

    /// <summary>True for a folder; the list uses it to pick the double-click behaviour.</summary>
    public bool IsFolder => Info.Kind == EntryKind.Folder;

    /// <summary>The comment stored with the entry; empty for most entries.</summary>
    public string Comment => Info.Comment;

    /// <summary>Replaces the snapshot after a change, keeping the row identity.</summary>
    /// <param name="info">The fresh snapshot; it must be the same entry.</param>
    public void Update(EntryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (Info == info)
        {
            return;
        }

        Info = info;
    }

    /// <summary>How many bullets a masked name shows, whatever its real length.</summary>
    public const int MaskLength = 8;

    /// <summary>
    /// Replaces a name with a fixed run of bullets for panic mode. The run is the same width for
    /// every name: a mask whose length followed the name's told a shoulder-surfer exactly how
    /// long each name was and left the long ones conspicuous, which is more than the gesture
    /// implies.
    /// </summary>
    /// <param name="name">The real name; only its presence matters.</param>
    public static string MaskName(string? name) => new('•', MaskLength);

    private static string Mask(string name) => MaskName(name);

    partial void OnInfoChanged(EntryInfo value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(RealName));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ChildCount));
        OnPropertyChanged(nameof(ModifiedUtc));
        OnPropertyChanged(nameof(CreatedUtc));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Comment));
    }

    partial void OnIsMaskedChanged(bool value) => OnPropertyChanged(nameof(Name));
}
