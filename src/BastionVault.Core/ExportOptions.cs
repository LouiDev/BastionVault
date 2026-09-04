namespace BastionVault.Core;

/// <summary>Settings for <see cref="IVaultSession.ExportAsync"/> and <see cref="IVaultSession.RecoverAsync"/> (FORMAT.md §8.7).</summary>
/// <param name="Conflict">What to do when the destination name already exists on disk.</param>
/// <param name="RestoreTimestamps">Restore the creation and modification times stored in the vault.</param>
/// <param name="MarkOfTheWeb">Write the <c>Zone.Identifier</c> alternate stream with <c>ZoneId=3</c>.</param>
/// <param name="WritePartialFiles">Recover only: write the authenticated prefix of a damaged file as <c>name.partial</c>.</param>
public sealed record ExportOptions(
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    bool RestoreTimestamps = true,
    bool MarkOfTheWeb = true,
    bool WritePartialFiles = false);

/// <summary>One entry of the continue-on-error export report.</summary>
/// <param name="VaultPath">In-vault path of the entry, for example <c>\Docs\a.txt</c>.</param>
/// <param name="Kind">What happened to it.</param>
/// <param name="Detail">Extra information, for example the destination name or the underlying error text.</param>
/// <param name="ChunkIndex">Chunk that failed authentication, when applicable.</param>
public sealed record ExportIssue(string VaultPath, ExportIssueKind Kind, string? Detail, uint? ChunkIndex);

/// <summary>Outcome of an export or recover run.</summary>
/// <param name="FilesWritten">Number of files written to disk.</param>
/// <param name="FoldersCreated">Number of folders created on disk (including empty ones).</param>
/// <param name="BytesWritten">Plaintext bytes written.</param>
/// <param name="Issues">Everything that did not export verbatim.</param>
public sealed record ExportResult(int FilesWritten, int FoldersCreated, long BytesWritten, IReadOnlyList<ExportIssue> Issues);
