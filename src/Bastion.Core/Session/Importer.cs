using System.Buffers;
using System.Security.Cryptography;
using Bastion.Core.Crypto;
using Bastion.Core.Format;
using IoPath = System.IO.Path;

namespace Bastion.Core.Session;

/// <summary>What an import produced; the session attaches it to the tree and records the undo step.</summary>
/// <param name="Created">Every node created, in creation order.</param>
/// <param name="Roots">The created nodes whose parent existed before the import.</param>
/// <param name="Replaced">Nodes that were removed because the conflict policy said so.</param>
/// <param name="BytesImported">Plaintext bytes staged.</param>
/// <param name="Issues">Everything that did not import verbatim.</param>
internal sealed record ImportOutcome(
    IReadOnlyList<TreeNode> Created,
    IReadOnlyList<(TreeNode Node, TreeNode Parent)> Roots,
    IReadOnlyList<(TreeNode Node, TreeNode Parent)> Replaced,
    long BytesImported,
    IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Imports files and folders from disk (FORMAT.md section 8.6): an iterative walk that never follows a
/// reparse point, content encrypted straight into staging under its final blob key, continue-on-error
/// with a report, and a cancellation that leaves neither staged bytes nor tree changes behind.
/// </summary>
internal sealed class Importer
{
    private readonly VaultSession _session;
    private readonly List<ImportIssue> _issues = [];
    private readonly List<TreeNode> _created = [];
    private readonly HashSet<TreeNode> _createdSet = [];
    private readonly List<(TreeNode Node, TreeNode Parent)> _roots = [];
    private readonly List<(TreeNode Node, TreeNode Parent)> _replaced = [];
    private readonly List<StagedBlobSource> _staged = [];

    private ConflictPolicy _policy;
    private long _bytesImported;

    /// <summary>Creates an importer for one call.</summary>
    /// <param name="session">The session that owns the tree, the staging store and the keys.</param>
    public Importer(VaultSession session) => _session = session;

    /// <summary>Runs the import.</summary>
    /// <param name="parent">Destination folder.</param>
    /// <param name="sourcePaths">Files and directories to import.</param>
    /// <param name="options">Conflict handling, timestamps and depth limit.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ImportOutcome> RunAsync(
        TreeNode parent,
        IReadOnlyList<string> sourcePaths,
        ImportOptions options,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        _policy = options.Conflict;

        List<PlanItem> plan = BuildPlan(parent, sourcePaths, options);
        long ciphertextBytes = 0;
        long plaintextBytes = 0;
        foreach (PlanItem item in plan)
        {
            if (!item.IsDirectory)
            {
                plaintextBytes += item.Length;
                ciphertextBytes += ChunkCipher.BlobLength(item.Length, VaultLimits.DefaultChunkSize);
            }
        }

        _session.Staging.PreflightSpace(ciphertextBytes, _session.EstimatedVaultLength + ciphertextBytes);

        var throttle = new ProgressThrottle(progress, VaultOperation.Import, plaintextBytes, plan.Count);
        throttle.Start();

        try
        {
            await ExecuteAsync(parent, plan, options, throttle, ct).ConfigureAwait(false);
        }
        catch
        {
            RollBack();
            throw;
        }

        throttle.Complete(_bytesImported, plan.Count);
        return new ImportOutcome(_created, _roots, _replaced, _bytesImported, _issues);
    }

    /// <summary>Walks the sources iteratively and records what is to be imported.</summary>
    /// <param name="parent">Destination folder.</param>
    /// <param name="sourcePaths">Files and directories to import.</param>
    /// <param name="options">Import options.</param>
    private List<PlanItem> BuildPlan(TreeNode parent, IReadOnlyList<string> sourcePaths, ImportOptions options)
    {
        var plan = new List<PlanItem>();
        int baseDepth = TreeModel.DepthOf(parent);
        int maxDepth = Math.Min(options.MaxDepth, VaultLimits.MaxDepth);

        var pending = new Stack<(string Path, int ParentIndex, int Depth)>();
        for (int i = sourcePaths.Count - 1; i >= 0; i--)
        {
            pending.Push((sourcePaths[i], -1, 1));
        }

        while (pending.Count > 0)
        {
            (string path, int parentIndex, int depth) = pending.Pop();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _issues.Add(new ImportIssue(path, ImportIssueKind.Unreadable, ex.Message));
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                _issues.Add(new ImportIssue(path, ImportIssueKind.SkippedReparsePoint, "junctions and symlinks are never followed"));
                continue;
            }

            if (depth > maxDepth || baseDepth + depth > VaultLimits.MaxDepth)
            {
                _issues.Add(new ImportIssue(path, ImportIssueKind.TooDeep, $"the destination would sit at depth {baseDepth + depth}"));
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                int index = plan.Count;
                plan.Add(new PlanItem
                {
                    SourcePath = path,
                    IsDirectory = true,
                    ParentIndex = parentIndex,
                });

                foreach (string child in EnumerateChildren(path))
                {
                    pending.Push((child, index, depth + 1));
                }

                continue;
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _issues.Add(new ImportIssue(path, ImportIssueKind.Unreadable, ex.Message));
                continue;
            }

            plan.Add(new PlanItem
            {
                SourcePath = path,
                IsDirectory = false,
                Length = length,
                ParentIndex = parentIndex,
            });
        }

        return plan;
    }

    /// <summary>Lists the children of a directory, in a stable order, reporting what cannot be read.</summary>
    /// <param name="directory">Directory to list.</param>
    private IEnumerable<string> EnumerateChildren(string directory)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.None,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _issues.Add(new ImportIssue(directory, ImportIssueKind.Unreadable, ex.Message));
            return [];
        }

        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
        Array.Reverse(entries);
        return entries;
    }

    /// <summary>Creates the entries and stages their content.</summary>
    /// <param name="parent">Destination folder.</param>
    /// <param name="plan">The planned items.</param>
    /// <param name="options">Import options.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ExecuteAsync(TreeNode parent, List<PlanItem> plan, ImportOptions options, ProgressThrottle throttle, CancellationToken ct)
    {
        var folders = new Dictionary<int, TreeNode>();

        for (int i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PlanItem item = plan[i];

            TreeNode? destination = parent;
            if (item.ParentIndex >= 0 && !folders.TryGetValue(item.ParentIndex, out destination))
            {
                // The parent folder was skipped or failed, so its whole subtree is skipped silently:
                // the parent already produced the issue that explains it.
                continue;
            }
            string rawName = IoPath.GetFileName(item.SourcePath.TrimEnd(IoPath.DirectorySeparatorChar, IoPath.AltDirectorySeparatorChar));
            if (rawName.Length == 0)
            {
                rawName = item.SourcePath;
            }

            string name = EntryNames.Sanitize(rawName);
            if (!string.Equals(name, rawName, StringComparison.Ordinal))
            {
                _issues.Add(new ImportIssue(item.SourcePath, ImportIssueKind.Renamed, name));
            }

            ResolvedName resolved = await ResolveConflictAsync(destination, name, item, options, ct).ConfigureAwait(false);
            if (resolved.Skip)
            {
                _issues.Add(new ImportIssue(item.SourcePath, ImportIssueKind.Skipped, "skipped: an entry with that name already exists"));
                continue;
            }

            if (resolved.Merge is TreeNode existingFolder)
            {
                folders[i] = existingFolder;
                continue;
            }

            if (!string.Equals(resolved.Name, name, StringComparison.Ordinal))
            {
                _issues.Add(new ImportIssue(item.SourcePath, ImportIssueKind.Renamed, resolved.Name));
            }

            if (resolved.Replace is TreeNode victim)
            {
                _replaced.Add((victim, destination));
                _session.DetachSubtree(victim);
            }

            if (item.IsDirectory)
            {
                TreeNode folder = CreateNode(destination, resolved.Name, EntryKind.Folder, item.SourcePath, options);
                folders[i] = folder;
                continue;
            }

            await ImportFileAsync(destination, resolved.Name, item, options, throttle, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Applies the conflict policy or asks the resolver.</summary>
    /// <param name="destination">Destination folder.</param>
    /// <param name="name">Sanitised name.</param>
    /// <param name="item">The item being imported.</param>
    /// <param name="options">Import options.</param>
    /// <param name="ct">Cancellation token.</param>
    private async ValueTask<ResolvedName> ResolveConflictAsync(
        TreeNode destination, string name, PlanItem item, ImportOptions options, CancellationToken ct)
    {
        TreeNode? existing = TreeModel.FindChild(destination, name);
        if (existing is null)
        {
            return ResolvedName.Use(name);
        }

        // Two folders with the same name are merged; that is what every file manager does.
        if (item.IsDirectory && existing.Kind == EntryKind.Folder)
        {
            return ResolvedName.MergeInto(existing);
        }

        ConflictPolicy policy = _policy;
        if (options.ConflictResolver is not null)
        {
            var context = new ConflictContext(
                new EntryId(destination.Id), name, _session.Tree.Snapshot(existing), item.SourcePath, item.Length);

            ConflictDecision decision = await options.ConflictResolver(context, ct).ConfigureAwait(false);
            switch (decision)
            {
                case ConflictDecision.Cancel:
                    throw new OperationCanceledException(ct);
                case ConflictDecision.RenameAll:
                    _policy = policy = ConflictPolicy.Rename;
                    break;
                case ConflictDecision.ReplaceAll:
                    _policy = policy = ConflictPolicy.Replace;
                    break;
                case ConflictDecision.SkipAll:
                    _policy = policy = ConflictPolicy.Skip;
                    break;
                case ConflictDecision.Rename:
                    policy = ConflictPolicy.Rename;
                    break;
                case ConflictDecision.Replace:
                    policy = ConflictPolicy.Replace;
                    break;
                default:
                    policy = ConflictPolicy.Skip;
                    break;
            }
        }

        return policy switch
        {
            ConflictPolicy.Replace => ResolvedName.Overwrite(name, existing),
            ConflictPolicy.Skip => ResolvedName.Skipped(),
            _ => ResolvedName.Use(VaultSession.UniqueSiblingName(name, destination)),
        };
    }

    /// <summary>Reads a source file, encrypts it into staging and creates its entry.</summary>
    /// <param name="destination">Destination folder.</param>
    /// <param name="name">Name inside the vault.</param>
    /// <param name="item">The planned item.</param>
    /// <param name="options">Import options.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ImportFileAsync(
        TreeNode destination, string name, PlanItem item, ImportOptions options, ProgressThrottle throttle, CancellationToken ct)
    {
        FileStat? before = FileIo.TryStat(item.SourcePath);
        if (before is null)
        {
            _issues.Add(new ImportIssue(item.SourcePath, ImportIssueKind.Unreadable, "the file disappeared before it could be read"));
            return;
        }

        byte[] blobId = new byte[16];
        _session.Random.Fill(blobId);

        StagedBlobSource slot = _session.Staging.BeginBlob();
        _staged.Add(slot);

        long length;
        byte[] hash;
        try
        {
            (length, hash) = await EncryptIntoStagingAsync(item, blobId, slot, throttle, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or VaultResourceException)
        {
            // A cancelled import or a full disk aborts the whole run; the caller rolls everything back.
            throw;
        }
        catch (Exception ex)
        {
            _session.Staging.EndBlob(slot);
            _session.Staging.Discard([slot]);
            _staged.Remove(slot);
            _issues.Add(new ImportIssue(item.SourcePath, ClassifyReadFailure(ex), ex.Message));
            return;
        }

        _session.Staging.EndBlob(slot);

        FileStat? after = FileIo.TryStat(item.SourcePath);
        if (after is null || after.Value.Length != length || after.Value.LastWriteUtc != before.Value.LastWriteUtc)
        {
            _session.Staging.Discard([slot]);
            _staged.Remove(slot);
            _issues.Add(new ImportIssue(item.SourcePath, ImportIssueKind.ChangedWhileReading, "the file changed while it was being read"));
            return;
        }

        CreateNode(destination, name, EntryKind.File, item.SourcePath, options, new BlobRef
        {
            BlobId = blobId,
            Source = slot,
            Length = length,
            ChunkSize = VaultLimits.DefaultChunkSize,
            BlobHash = hash,
        });

        _bytesImported += length;
        throttle.Report(_bytesImported, _created.Count, name);
    }

    /// <summary>
    /// Streams one file into staging, encrypting chunk by chunk with a one-chunk lookahead so the
    /// last-chunk flag is correct even for a file whose length is only known while it is read.
    /// </summary>
    /// <param name="item">The planned item.</param>
    /// <param name="blobId">The fresh blob id.</param>
    /// <param name="slot">The staging slot.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<(long Length, byte[] Hash)> EncryptIntoStagingAsync(
        PlanItem item, byte[] blobId, StagedBlobSource slot, ProgressThrottle throttle, CancellationToken ct)
    {
        const int chunkSize = (int)VaultLimits.DefaultChunkSize;

        byte[] current = ArrayPool<byte>.Shared.Rent(chunkSize);
        byte[] next = ArrayPool<byte>.Shared.Rent(chunkSize);
        byte[] cipher = ArrayPool<byte>.Shared.Rent(chunkSize + ChunkCipher.TagSize);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using KeyMaterial blobKey = VaultKeys.DeriveBlobKey(_session.RequireCrypto().VaultKey.Span, blobId);
        using var aes = new AesGcm(blobKey.Span, ChunkCipher.TagSize);

        var source = new FileStream(item.SourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Write | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
        });

        try
        {
            long total = 0;
            uint index = 0;
            int filled = await ReadBlockAsync(source, current, ct).ConfigureAwait(false);

            while (true)
            {
                int lookahead = filled == chunkSize ? await ReadBlockAsync(source, next, ct).ConfigureAwait(false) : 0;
                bool isLast = lookahead == 0;

                if (total + filled > VaultLimits.MaxFileLength)
                {
                    throw new IOException($"The file is larger than the {VaultLimits.MaxFileLength} bytes the format allows.");
                }

                ChunkCipher.EncryptChunk(
                    aes, _session.RequireCrypto().VaultId, blobId, index, isLast,
                    current.AsSpan(0, filled), cipher.AsSpan(0, filled + ChunkCipher.TagSize));

                hash.AppendData(cipher.AsSpan(0, filled + ChunkCipher.TagSize));
                _session.Staging.Append(slot, cipher.AsSpan(0, filled + ChunkCipher.TagSize));

                total += filled;
                index++;
                throttle.Report(_bytesImported + total, _created.Count, IoPath.GetFileName(item.SourcePath));

                if (isLast)
                {
                    break;
                }

                (current, next) = (next, current);
                filled = lookahead;
            }

            return (total, hash.GetHashAndReset());
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(current, clearArray: true);
            ArrayPool<byte>.Shared.Return(next, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    /// <summary>Reads until the buffer is full or the file ends.</summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<int> ReadBlockAsync(FileStream stream, byte[] buffer, CancellationToken ct)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(done), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            done += read;
        }

        return done;
    }

    /// <summary>Creates and attaches one entry.</summary>
    /// <param name="destination">Destination folder.</param>
    /// <param name="name">Entry name.</param>
    /// <param name="kind">Folder or file.</param>
    /// <param name="sourcePath">Source on disk, for the timestamps.</param>
    /// <param name="options">Import options.</param>
    /// <param name="content">Content of a file entry, or <see langword="null"/> for a folder.</param>
    private TreeNode CreateNode(
        TreeNode destination, string name, EntryKind kind, string sourcePath, ImportOptions options, BlobRef? content = null)
    {
        long created = TreeModel.ToTicks(_session.Clock.UtcNow);
        long modified = created;

        if (options.PreserveTimestamps)
        {
            try
            {
                created = ClampTicks(File.GetCreationTimeUtc(sourcePath).Ticks);
                modified = ClampTicks(File.GetLastWriteTimeUtc(sourcePath).Ticks);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall back to the session clock; a missing timestamp is not worth failing an import.
            }
        }

        var node = new TreeNode
        {
            Id = _session.Tree.AllocateId(),
            Kind = kind,
            Name = name,
            CreatedUtcTicks = created,
            ModifiedUtcTicks = modified,
            State = EntryState.Added,
            Content = content,
        };

        _session.AttachSubtree(node, destination);
        _created.Add(node);
        _createdSet.Add(node);
        if (!_createdSet.Contains(destination))
        {
            _roots.Add((node, destination));
        }

        return node;
    }

    /// <summary>Undoes everything this import did: the tree changes and the staged ciphertext.</summary>
    private void RollBack()
    {
        for (int i = _roots.Count - 1; i >= 0; i--)
        {
            if (_roots[i].Node.Parent is not null)
            {
                _session.DetachSubtree(_roots[i].Node);
            }
        }

        for (int i = _replaced.Count - 1; i >= 0; i--)
        {
            _session.AttachSubtree(_replaced[i].Node, _replaced[i].Parent);
        }

        _session.Staging.Discard(_staged);
        _staged.Clear();
        _created.Clear();
        _roots.Clear();
        _replaced.Clear();
    }

    /// <summary>Maps a read failure to the issue the report shows.</summary>
    /// <param name="exception">The caught exception.</param>
    private static ImportIssueKind ClassifyReadFailure(Exception exception) =>
        exception is IOException io && IoGuard.CodeFor(io) == VaultErrorCode.Locked
            ? ImportIssueKind.Locked
            : ImportIssueKind.Unreadable;

    /// <summary>Clamps a tick count into the range the format allows.</summary>
    /// <param name="ticks">Tick count.</param>
    private static long ClampTicks(long ticks) => ticks is < 0 or > TreeModel.MaxTicks ? 0 : ticks;

    /// <summary>One planned item of the walk.</summary>
    private sealed class PlanItem
    {
        /// <summary>Absolute path on disk.</summary>
        public required string SourcePath { get; init; }

        /// <summary>True for a directory.</summary>
        public required bool IsDirectory { get; init; }

        /// <summary>Length of a file at planning time.</summary>
        public long Length { get; init; }

        /// <summary>Index of the planned directory this item lives in, or -1 for a top-level source.</summary>
        public required int ParentIndex { get; init; }
    }

    /// <summary>The outcome of applying the conflict policy to one name.</summary>
    private readonly struct ResolvedName
    {
        /// <summary>The name to use.</summary>
        public string Name { get; private init; }

        /// <summary>True when the item is not imported at all.</summary>
        public bool Skip { get; private init; }

        /// <summary>An existing folder the item is merged into.</summary>
        public TreeNode? Merge { get; private init; }

        /// <summary>An existing entry that is replaced.</summary>
        public TreeNode? Replace { get; private init; }

        /// <summary>Import under this name.</summary>
        /// <param name="name">The name.</param>
        public static ResolvedName Use(string name) => new() { Name = name };

        /// <summary>Merge into an existing folder.</summary>
        /// <param name="folder">The folder to merge into.</param>
        public static ResolvedName MergeInto(TreeNode folder) => new() { Name = folder.Name, Merge = folder };

        /// <summary>Replace an existing entry.</summary>
        /// <param name="name">The name.</param>
        /// <param name="existing">The entry to remove.</param>
        public static ResolvedName Overwrite(string name, TreeNode existing) => new() { Name = name, Replace = existing };

        /// <summary>Skip the item.</summary>
        public static ResolvedName Skipped() => new() { Name = string.Empty, Skip = true };
    }
}
