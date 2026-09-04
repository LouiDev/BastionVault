namespace Bastion.Core.Session;

/// <summary>Import, export, preview, verify and recover.</summary>
internal sealed partial class VaultSession
{
    /// <inheritdoc />
    public async Task<ImportResult> ImportAsync(
        EntryId parent,
        IReadOnlyList<string> sourcePaths,
        ImportOptions options,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(options);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        TreeNode folder;
        lock (_treeGate)
        {
            folder = RequireFolder(parent, nameof(parent));
        }

        ImportOutcome outcome;
        try
        {
            outcome = await new Importer(this).RunAsync(folder, sourcePaths, options, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, null);
        }

        var imported = new List<EntryId>(outcome.Created.Count);
        foreach (TreeNode node in outcome.Created)
        {
            imported.Add(new EntryId(node.Id));
        }

        if (outcome.Created.Count == 0 && outcome.Replaced.Count == 0)
        {
            return new ImportResult(imported, outcome.BytesImported, outcome.Issues);
        }

        lock (_treeGate)
        {
            _undo.Push(new AddEntriesStep(
                outcome.Roots,
                outcome.Replaced,
                outcome.Created.Count == 1 ? $"Import {outcome.Created[0].Name}" : $"Import {outcome.Created.Count} entries"));
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, imported, parent);
        return new ImportResult(imported, outcome.BytesImported, outcome.Issues);
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<EntryId> entries,
        string destinationDirectory,
        ExportOptions options,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(options);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireUnlocked();

        var roots = new List<TreeNode>(entries.Count);
        lock (_treeGate)
        {
            foreach (EntryId id in entries)
            {
                TreeNode node = RequireNode(id, nameof(entries));
                if (node.Id != 0)
                {
                    roots.Add(node);
                }
            }
        }

        try
        {
            return await new Exporter(this)
                .RunAsync(roots, destinationDirectory, options, VaultOperation.Export, progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, destinationDirectory);
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(EntryId file, CancellationToken ct)
    {
        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireUnlocked();

        BlobRef content;
        string vaultPath;
        lock (_treeGate)
        {
            TreeNode node = RequireNode(file, nameof(file));
            if (node.Kind != EntryKind.File || node.Content is null)
            {
                throw new ArgumentException($"Entry {file.Value} is not a file.", nameof(file));
            }

            content = node.Content;
            vaultPath = TreeModel.FormatPath(node);
        }

        return Task.FromResult<Stream>(new DecryptingBlobStream(OpenBlobReader(content, vaultPath)));
    }

    /// <inheritdoc />
    public async Task<VerifyReport> VerifyAsync(IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireUnlocked();

        List<TreeNode> files;
        StoredLayout layout;
        lock (_treeGate)
        {
            files = [];
            foreach (TreeNode node in Tree.CanonicalOrder())
            {
                if (node.Kind == EntryKind.File)
                {
                    files.Add(node);
                }
            }

            layout = _layout;
        }

        try
        {
            return await Task.Run(() => new Verifier(this).Run(files, layout, progress, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, Path);
        }
    }

    /// <inheritdoc />
    public async Task<ExportResult> RecoverAsync(
        string destinationDirectory,
        ExportOptions options,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(options);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireUnlocked();

        List<TreeNode> roots;
        lock (_treeGate)
        {
            roots = TreeModel.OrderedChildren(Tree.Root);
        }

        try
        {
            return await new Exporter(this)
                .RunAsync(roots, destinationDirectory, options, VaultOperation.Recover, progress, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, destinationDirectory);
        }
    }
}
