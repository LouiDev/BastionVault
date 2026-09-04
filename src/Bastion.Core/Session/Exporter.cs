using System.Buffers;
using System.Text;
using Bastion.Core.Crypto;
using Bastion.Core.Format;
using IoPath = System.IO.Path;

namespace Bastion.Core.Session;

/// <summary>
/// Writes entries back to disk (FORMAT.md section 8.7 with the safety rules of section 6.4): every
/// destination is proved to stay inside the export root, content is streamed into a temporary sibling
/// and renamed on success, and a chunk that fails authentication never leaves a whole file behind.
/// </summary>
internal sealed class Exporter
{
    private const int CopyBufferSize = 1 << 20;

    private readonly VaultSession _session;
    private readonly List<ExportIssue> _issues = [];

    /// <summary>Relative paths whose whole subtree is refused, so no descendant may be written.</summary>
    private readonly List<string> _refusedSubtrees = [];

    /// <summary>Directory components already proved not to be reparse points, under the export root.</summary>
    private readonly HashSet<string> _traversableDirectories = new(StringComparer.OrdinalIgnoreCase);

    private int _filesWritten;
    private int _foldersCreated;
    private long _bytesWritten;

    /// <summary>Creates an exporter for one call.</summary>
    /// <param name="session">The session that owns the tree and the keys.</param>
    public Exporter(VaultSession session) => _session = session;

    /// <summary>Exports a set of entries.</summary>
    /// <param name="roots">Entries to export; their subtrees follow.</param>
    /// <param name="destinationDirectory">Export root.</param>
    /// <param name="options">Conflict handling, timestamps, Mark of the Web and partial files.</param>
    /// <param name="operation">Export or Recover, for the progress reports.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ExportResult> RunAsync(
        IReadOnlyList<TreeNode> roots,
        string destinationDirectory,
        ExportOptions options,
        VaultOperation operation,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        string root = IoPath.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(LongPath.ForIo(root));

        List<(TreeNode Node, string Relative)> plan = BuildPlan(roots);
        long totalBytes = 0;
        int totalFiles = 0;
        foreach ((TreeNode node, string _) in plan)
        {
            if (node.Kind == EntryKind.File)
            {
                totalFiles++;
                totalBytes += node.Content?.Length ?? 0;
            }
        }

        var throttle = new ProgressThrottle(progress, operation, totalBytes, totalFiles);
        throttle.Start();

        foreach ((TreeNode node, string relative) in plan)
        {
            ct.ThrowIfCancellationRequested();

            // A refused folder takes its whole subtree with it. Refusing only the leaf let the
            // descendants be written straight through a junction, outside the export root.
            if (IsUnderRefusedSubtree(relative))
            {
                continue;
            }

            string? destination = ResolveDestination(root, relative, node);
            if (destination is null)
            {
                RefuseSubtree(relative);
                continue;
            }

            if (node.Kind == EntryKind.Folder)
            {
                if (!CreateFolder(node, destination))
                {
                    RefuseSubtree(relative);
                }

                continue;
            }

            await ExportFileAsync(node, destination, options, throttle, ct).ConfigureAwait(false);
        }

        throttle.Complete(_bytesWritten, _filesWritten);
        return new ExportResult(_filesWritten, _foldersCreated, _bytesWritten, _issues);
    }

    /// <summary>Flattens the subtrees into a list of entries with their path relative to the export root.</summary>
    /// <param name="roots">Entries to export.</param>
    private static List<(TreeNode Node, string Relative)> BuildPlan(IReadOnlyList<TreeNode> roots)
    {
        var plan = new List<(TreeNode, string)>();
        foreach (TreeNode root in roots)
        {
            var stack = new Stack<(TreeNode Node, string Relative)>();
            stack.Push((root, root.Name));
            while (stack.Count > 0)
            {
                (TreeNode node, string relative) = stack.Pop();
                plan.Add((node, relative));

                List<TreeNode> children = TreeModel.OrderedChildren(node);
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push((children[i], IoPath.Combine(relative, children[i].Name)));
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Builds and checks the destination path (FORMAT.md section 6.4): combined, fully normalised and
    /// proved to stay under the export root.
    /// </summary>
    /// <param name="root">Fully qualified export root.</param>
    /// <param name="relative">Path relative to the root.</param>
    /// <param name="node">Entry being exported, for the report.</param>
    private string? ResolveDestination(string root, string relative, TreeNode node)
    {
        string vaultPath = TreeModel.FormatPath(node);
        try
        {
            string combined = IoPath.GetFullPath(IoPath.Combine(root, relative));
            string prefix = root.EndsWith(IoPath.DirectorySeparatorChar) ? root : root + IoPath.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, "the destination would leave the export folder", null));
                return null;
            }

            if (LongPath.ForIo(combined).Length > LongPath.MaxExtendedPath)
            {
                _issues.Add(new ExportIssue(
                    vaultPath, ExportIssueKind.PathTooLong, $"the destination path is {combined.Length} characters long", null));
                return null;
            }

            // The prefix test only compares strings. A directory component on the way can be a junction
            // that redirects the whole path somewhere else, so every component below the root that
            // already exists has to be proved to be a real directory (FORMAT.md section 6.4).
            if (HasReparsePointComponent(prefix, combined))
            {
                _issues.Add(new ExportIssue(
                    vaultPath, ExportIssueKind.ReparsePointRefused, IoPath.GetDirectoryName(combined) ?? combined, null));
                return null;
            }

            return combined;
        }
        catch (PathTooLongException ex)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.PathTooLong, ex.Message, null));
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, ex.Message, null));
            return null;
        }
    }

    /// <summary>Creates a folder on disk, including the empty ones.</summary>
    /// <param name="node">The folder entry.</param>
    /// <param name="destination">Destination path.</param>
    /// <returns>False when nothing below this folder may be written either.</returns>
    private bool CreateFolder(TreeNode node, string destination)
    {
        string vaultPath = TreeModel.FormatPath(node);
        try
        {
            if (IsReparsePoint(destination))
            {
                _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.ReparsePointRefused, destination, null));
                return false;
            }

            if (!Directory.Exists(LongPath.ForIo(destination)))
            {
                Directory.CreateDirectory(LongPath.ForIo(destination));
                _foldersCreated++;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, ex.Message, null));
            return false;
        }
    }

    /// <summary>True when a plan item sits under a folder whose export was refused.</summary>
    /// <param name="relative">Path of the item relative to the export root.</param>
    private bool IsUnderRefusedSubtree(string relative)
    {
        foreach (string refused in _refusedSubtrees)
        {
            if (relative.Length > refused.Length &&
                relative.StartsWith(refused, StringComparison.OrdinalIgnoreCase) &&
                (relative[refused.Length] == IoPath.DirectorySeparatorChar ||
                 relative[refused.Length] == IoPath.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks a relative path and everything under it as not to be written.</summary>
    /// <param name="relative">Path of the refused item relative to the export root.</param>
    private void RefuseSubtree(string relative) => _refusedSubtrees.Add(relative);

    /// <summary>
    /// True when any existing directory component strictly between the export root and the destination
    /// is a reparse point. Results are cached: the plan visits a folder's children right after it.
    /// </summary>
    /// <param name="rootPrefix">The export root with a trailing separator.</param>
    /// <param name="destination">Fully qualified destination inside the root.</param>
    private bool HasReparsePointComponent(string rootPrefix, string destination)
    {
        var pending = new List<string>();
        string? component = IoPath.GetDirectoryName(destination);
        while (component is not null &&
               component.Length >= rootPrefix.Length &&
               !_traversableDirectories.Contains(component))
        {
            pending.Add(component);
            component = IoPath.GetDirectoryName(component);
        }

        // Outermost first: a junction nearer the root makes everything below it unreachable anyway.
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (IsReparsePoint(pending[i]))
            {
                return true;
            }

            _traversableDirectories.Add(pending[i]);
        }

        return false;
    }

    /// <summary>Streams one file to disk.</summary>
    /// <param name="node">The file entry.</param>
    /// <param name="destination">Destination path.</param>
    /// <param name="options">Export options.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ExportFileAsync(
        TreeNode node, string destination, ExportOptions options, ProgressThrottle throttle, CancellationToken ct)
    {
        string vaultPath = TreeModel.FormatPath(node);
        BlobRef? content = node.Content;
        if (content is null)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, "the entry has no content", null));
            return;
        }

        string? target = ApplyConflictPolicy(destination, options, vaultPath);
        if (target is null)
        {
            return;
        }

        if (IsReparsePoint(target))
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.ReparsePointRefused, target, null));
            return;
        }

        string tempPath = target + ".tmp-" + _session.NewSuffix();
        bool tempConsumed = false;
        uint? failedChunk = null;
        long written = 0;

        try
        {
            using BlobReader reader = _session.OpenBlobReader(content, vaultPath);
            var stream = new FileStream(LongPath.ForIo(tempPath), new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                BufferSize = 0,
                PreallocationSize = content.Length,
            });

            await using (stream.ConfigureAwait(false))
            {
                byte[] cipher = ArrayPool<byte>.Shared.Rent(reader.MaxChunkCiphertextLength);
                byte[] plain = ArrayPool<byte>.Shared.Rent(Math.Max(reader.MaxChunkPlaintextLength, 1));
                try
                {
                    for (uint chunk = 0; chunk < reader.ChunkCount; chunk++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int length;
                        try
                        {
                            length = reader.ReadPlaintextChunk(chunk, cipher, plain);
                        }
                        catch (VaultIntegrityException)
                        {
                            failedChunk = chunk;
                            break;
                        }

                        await stream.WriteAsync(plain.AsMemory(0, length), ct).ConfigureAwait(false);
                        written += length;
                        _bytesWritten += length;
                        throttle.Report(_bytesWritten, _filesWritten, node.Name);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(cipher);
                    ArrayPool<byte>.Shared.Return(plain, clearArray: true);
                }
            }

            if (failedChunk is uint bad)
            {
                _bytesWritten -= written;
                HandleIntegrityFailure(vaultPath, tempPath, target, options, bad);
                tempConsumed = true;
                return;
            }

            Finish(tempPath, target, node, options);
            tempConsumed = true;
            _filesWritten++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (VaultException ex)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, ex.Message, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IoError, ex.Message, null));
        }
        finally
        {
            if (!tempConsumed)
            {
                TryDelete(tempPath);
            }
        }
    }

    /// <summary>Applies the conflict policy to a destination that already exists.</summary>
    /// <param name="destination">Wanted destination path.</param>
    /// <param name="options">Export options.</param>
    /// <param name="vaultPath">In-vault path, for the report.</param>
    /// <returns>The path to write, or <see langword="null"/> when the entry is skipped.</returns>
    private string? ApplyConflictPolicy(string destination, ExportOptions options, string vaultPath)
    {
        string probe = LongPath.ForIo(destination);
        if (!File.Exists(probe) && !Directory.Exists(probe))
        {
            return destination;
        }

        switch (options.Conflict)
        {
            case ConflictPolicy.Replace:
                return destination;

            case ConflictPolicy.Skip:
                _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.Skipped, destination, null));
                return null;

            default:
                string directory = IoPath.GetDirectoryName(destination) ?? string.Empty;
                string unique = EntryNames.MakeUnique(
                    IoPath.GetFileName(destination),
                    candidate => File.Exists(LongPath.ForIo(IoPath.Combine(directory, candidate))) ||
                                 Directory.Exists(LongPath.ForIo(IoPath.Combine(directory, candidate))));
                string renamed = IoPath.Combine(directory, unique);
                _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.Renamed, unique, null));
                return renamed;
        }
    }

    /// <summary>Renames the finished temporary file into place and applies timestamps and Mark of the Web.</summary>
    /// <param name="tempPath">The finished temporary file.</param>
    /// <param name="target">Final destination.</param>
    /// <param name="node">The entry being exported.</param>
    /// <param name="options">Export options.</param>
    private void Finish(string tempPath, string target, TreeNode node, ExportOptions options)
    {
        File.Move(LongPath.ForIo(tempPath), LongPath.ForIo(target), overwrite: options.Conflict == ConflictPolicy.Replace);

        // Mark of the Web goes first: writing the alternate stream touches the modification time.
        if (options.MarkOfTheWeb)
        {
            TryMarkOfTheWeb(target);
        }

        if (options.RestoreTimestamps)
        {
            TryRestoreTimestamps(target, node);
        }
    }

    /// <summary>
    /// Deals with a chunk that did not authenticate: the partial output is deleted, or kept as
    /// <c>name.partial</c> when the caller asked for it (Recover).
    /// </summary>
    /// <param name="vaultPath">In-vault path, for the report.</param>
    /// <param name="tempPath">The partial output.</param>
    /// <param name="target">The destination the file would have had.</param>
    /// <param name="options">Export options.</param>
    /// <param name="chunk">The chunk that failed.</param>
    private void HandleIntegrityFailure(string vaultPath, string tempPath, string target, ExportOptions options, uint chunk)
    {
        if (!options.WritePartialFiles)
        {
            TryDelete(tempPath);
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IntegrityFailure, "the partial output was deleted", chunk));
            return;
        }

        string partial = target + ".partial";
        try
        {
            File.Move(LongPath.ForIo(tempPath), LongPath.ForIo(partial), overwrite: true);
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.PartialWritten, partial, chunk));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            _issues.Add(new ExportIssue(vaultPath, ExportIssueKind.IntegrityFailure, ex.Message, chunk));
        }
    }

    /// <summary>Restores the timestamps stored in the vault.</summary>
    /// <param name="path">File on disk.</param>
    /// <param name="node">The entry.</param>
    private static void TryRestoreTimestamps(string path, TreeNode node)
    {
        try
        {
            File.SetCreationTimeUtc(LongPath.ForIo(path), TreeModel.ToUtc(node.CreatedUtcTicks).UtcDateTime);
            File.SetLastWriteTimeUtc(LongPath.ForIo(path), TreeModel.ToUtc(node.ModifiedUtcTicks).UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // Timestamps are cosmetic; a file system that refuses them must not fail the export.
        }
    }

    /// <summary>Writes the Mark of the Web alternate data stream.</summary>
    /// <param name="path">File on disk.</param>
    private static void TryMarkOfTheWeb(string path)
    {
        try
        {
            using var stream = new FileStream(
                LongPath.ForIo(path) + ":Zone.Identifier", FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] payload = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");
            stream.Write(payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Alternate data streams only exist on NTFS; their absence is not an export failure.
        }
    }

    /// <summary>True when the path exists and is a reparse point.</summary>
    /// <param name="path">Path to test.</param>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            string probe = LongPath.ForIo(path);
            return (File.Exists(probe) || Directory.Exists(probe)) &&
                   (File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(LongPath.ForIo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file must never mask the failure that caused it.
        }
    }
}
