namespace Bastion.Core;

/// <summary>Settings for <see cref="IVaultSession.ImportAsync"/> (FORMAT.md §8.6).</summary>
/// <param name="Conflict">Default policy when a name already exists in the destination folder.</param>
/// <param name="ConflictResolver">
/// Optional interactive resolver. When set it is asked per conflict and its answer overrides
/// <paramref name="Conflict"/>; the <c>*All</c> answers apply to the rest of the import.
/// </param>
/// <param name="PreserveTimestamps">Copy the source creation and modification times into the vault.</param>
/// <param name="MaxDepth">Maximum directory depth to walk; deeper items are reported as <see cref="ImportIssueKind.TooDeep"/>.</param>
public sealed record ImportOptions(
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    Func<ConflictContext, CancellationToken, ValueTask<ConflictDecision>>? ConflictResolver = null,
    bool PreserveTimestamps = true,
    int MaxDepth = 128);

/// <summary>The single conflict an <see cref="ImportOptions.ConflictResolver"/> is asked about.</summary>
/// <param name="Parent">Destination folder inside the vault.</param>
/// <param name="Name">Name the incoming item would take.</param>
/// <param name="Existing">The entry that already carries that name.</param>
/// <param name="SourcePath">Full path of the incoming item on disk.</param>
/// <param name="SourceLength">Length of the incoming item in bytes (0 for a folder).</param>
public sealed record ConflictContext(EntryId Parent, string Name, EntryInfo Existing, string SourcePath, long SourceLength);

/// <summary>One entry of the continue-on-error import report.</summary>
/// <param name="SourcePath">Full path of the item on disk.</param>
/// <param name="Kind">What happened to it.</param>
/// <param name="Detail">Extra information, for example the new name or the underlying error text.</param>
public sealed record ImportIssue(string SourcePath, ImportIssueKind Kind, string? Detail);

/// <summary>Outcome of an import.</summary>
/// <param name="Imported">Ids of the entries created at the destination (top-level items and their descendants).</param>
/// <param name="BytesImported">Plaintext bytes staged.</param>
/// <param name="Issues">Everything that did not import verbatim.</param>
public sealed record ImportResult(IReadOnlyList<EntryId> Imported, long BytesImported, IReadOnlyList<ImportIssue> Issues);
