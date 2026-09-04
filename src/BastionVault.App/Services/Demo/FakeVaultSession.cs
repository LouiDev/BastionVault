using BastionVault.Core;

namespace BastionVault.App.Services.Demo;

/// <summary>
/// An in-memory <see cref="IVaultSession"/> used by the <c>--demo</c> switch and by the App tests.
/// It behaves like a real session - snapshots, undo, dirty tracking, rate-limited progress with
/// realistic delays - but it holds no keys and touches no disk, so the whole UI can be driven and
/// screenshotted long before BastionVault.Core is finished.
/// </summary>
public sealed class FakeVaultSession : IVaultSession
{
    private readonly Dictionary<uint, Node> _nodes = [];
    private readonly Stack<UndoStep> _undo = new();
    private readonly Stack<UndoStep> _redo = new();
    private readonly Lock _gate = new();

    private uint _nextId = 1;
    private string _vaultIdHex;
    private bool _rekeyPending;
    private ulong _saveCounter;
    private DateTimeOffset _lastSaved;
    private bool _dirty;
    private bool _locked;
    private bool _busy;

    /// <summary>Creates a session over a sample tree.</summary>
    /// <param name="path">Path the session reports; no file is touched.</param>
    /// <param name="vaultIdHex">
    /// The identity to report, or <see langword="null"/> for a fresh one. Two sessions given the same
    /// value stand for two copies of one vault, which is what a rollback looks like from the App.
    /// </param>
    /// <param name="saveCounter">The save counter to start from; a lower one stands for an older copy.</param>
    public FakeVaultSession(string path, string? vaultIdHex = null, ulong saveCounter = 7)
    {
        Path = path;
        _vaultIdHex = vaultIdHex ?? NewVaultIdHex();
        _saveCounter = saveCounter;
        _lastSaved = DateTimeOffset.UtcNow.AddHours(-19);
        Seed();
    }

    /// <inheritdoc />
    public event EventHandler<VaultChangedEventArgs>? Changed;

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public string VaultIdHex
    {
        get
        {
            lock (_gate)
            {
                return _vaultIdHex;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsLocked => _locked;

    /// <inheritdoc />
    public bool IsDirty => _dirty;

    /// <inheritdoc />
    public bool IsBusy => _busy;

    /// <inheritdoc />
    public KdfParameters Kdf { get; private set; } = KdfParameters.Default;

    /// <inheritdoc />
    public VaultStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                int folders = _nodes.Values.Count(n => n.Kind == EntryKind.Folder);
                int files = _nodes.Values.Count(n => n.Kind == EntryKind.File);
                long plaintext = _nodes.Values.Where(n => n.Kind == EntryKind.File).Sum(n => n.Length);
                return new VaultStatistics(folders, files, plaintext, plaintext + (16 * 1024 * 1024),
                    _saveCounter, _lastSaved, OpenedFromIndexCopy: false);
            }
        }
    }

    /// <inheritdoc />
    public PendingChanges Pending
    {
        get
        {
            lock (_gate)
            {
                int added = _nodes.Values.Count(n => n.State == EntryState.Added);
                int changed = _nodes.Values.Count(n => n.State == EntryState.Changed);
                long bytes = _nodes.Values.Where(n => n.State != EntryState.Stored && n.Kind == EntryKind.File).Sum(n => n.Length);
                return new PendingChanges(added, changed, DeletedCount, bytes, CredentialChangePending, CredentialChangePending);
            }
        }
    }

    /// <inheritdoc />
    public bool CanUndo => _undo.Count > 0;

    /// <inheritdoc />
    public bool CanRedo => _redo.Count > 0;

    /// <inheritdoc />
    public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;

    /// <inheritdoc />
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    private int DeletedCount { get; set; }

    private bool CredentialChangePending { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> GetChildren(EntryId folder)
    {
        lock (_gate)
        {
            return
            [
                .. _nodes.Values
                    .Where(n => n.ParentId == folder.Value)
                    .OrderBy(n => n.Kind == EntryKind.Folder ? 0 : 1)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(ToInfo),
            ];
        }
    }

    /// <inheritdoc />
    public EntryInfo? Find(EntryId id)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(id.Value, out Node? node) ? ToInfo(node) : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> GetAncestors(EntryId id)
    {
        lock (_gate)
        {
            var chain = new List<EntryInfo>();
            uint current = id.Value;
            while (current != 0 && _nodes.TryGetValue(current, out Node? node))
            {
                chain.Insert(0, ToInfo(node));
                current = node.ParentId;
            }

            return chain;
        }
    }

    /// <inheritdoc />
    public string FormatPath(EntryId id)
    {
        IReadOnlyList<EntryInfo> chain = GetAncestors(id);
        return chain.Count == 0 ? "\\" : "\\" + string.Join('\\', chain.Select(c => c.Name));
    }

    /// <inheritdoc />
    public bool TryResolvePath(string vaultPath, out EntryId id)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);

        id = EntryId.Root;
        foreach (string segment in vaultPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            EntryInfo? next = GetChildren(id).FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                id = EntryId.Root;
                return false;
            }

            id = next.Id;
        }

        return true;
    }

    /// <inheritdoc />
    public NameCheck ValidateName(EntryId parent, string name, EntryId? ignoring = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new NameCheck(false, "A name cannot be empty.", null);
        }

        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return new NameCheck(false, "A name cannot contain \\ / : * ? \" < > |.", null);
        }

        bool taken = GetChildren(parent)
            .Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) && c.Id != (ignoring ?? EntryId.Root));

        return taken ? new NameCheck(false, "That name is already used here.", name + " (2)") : NameCheck.Ok;
    }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> Search(string nameSubstring, EntryId? scope, int maxResults, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nameSubstring);

        lock (_gate)
        {
            return
            [
                .. _nodes.Values
                    .Where(n => n.Name.Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                    .Take(maxResults)
                    .Select(ToInfo),
            ];
        }
    }

    /// <inheritdoc />
    public Task<EntryId> CreateFolderAsync(EntryId parent, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Node node;
        lock (_gate)
        {
            node = Add(parent.Value, EntryKind.Folder, name, 0, EntryState.Added);
        }

        PushUndo($"Create folder \"{name}\"", () => Remove(node.Id));
        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, [new EntryId(node.Id)], parent);
        return Task.FromResult(new EntryId(node.Id));
    }

    /// <inheritdoc />
    public Task RenameAsync(EntryId entry, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string previous;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(entry.Value, out Node? node))
            {
                throw new VaultOperationException(VaultErrorCode.IndexInvalid, "No such entry.");
            }

            previous = node.Name;
            node.Name = newName;
            if (node.State == EntryState.Stored)
            {
                node.State = EntryState.Changed;
            }
        }

        PushUndo($"Rename to \"{newName}\"", () =>
        {
            lock (_gate)
            {
                if (_nodes.TryGetValue(entry.Value, out Node? node))
                {
                    node.Name = previous;
                }
            }
        });

        MarkDirty();
        Raise(VaultChangeKind.EntryRenamed, [entry], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetCommentAsync(EntryId entry, string comment, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_nodes.TryGetValue(entry.Value, out Node? node))
            {
                node.Comment = comment;
                if (node.State == EntryState.Stored)
                {
                    node.State = EntryState.Changed;
                }
            }
        }

        MarkDirty();
        Raise(VaultChangeKind.EntryUpdated, [entry], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MoveAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (EntryId id in entries)
            {
                if (_nodes.TryGetValue(id.Value, out Node? node))
                {
                    node.ParentId = newParent.Value;
                    if (node.State == EntryState.Stored)
                    {
                        node.State = EntryState.Changed;
                    }
                }
            }
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesMoved, entries, newParent);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryId>> CopyAsync(IReadOnlyList<EntryId> entries, EntryId newParent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ct.ThrowIfCancellationRequested();

        var created = new List<EntryId>();
        lock (_gate)
        {
            foreach (EntryId id in entries)
            {
                if (_nodes.TryGetValue(id.Value, out Node? node))
                {
                    Node copy = Add(newParent.Value, node.Kind, node.Name + " (copy)", node.Length, EntryState.Added);
                    created.Add(new EntryId(copy.Id));
                }
            }
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, created, newParent);
        return Task.FromResult<IReadOnlyList<EntryId>>(created);
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyList<EntryId> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ct.ThrowIfCancellationRequested();

        var removed = new List<Node>();
        lock (_gate)
        {
            foreach (EntryId id in entries)
            {
                if (_nodes.TryGetValue(id.Value, out Node? node))
                {
                    removed.Add(node);
                    _nodes.Remove(id.Value);
                    DeletedCount++;
                }
            }
        }

        PushUndo($"Delete {removed.Count} item{(removed.Count == 1 ? string.Empty : "s")}", () =>
        {
            lock (_gate)
            {
                foreach (Node node in removed)
                {
                    _nodes[node.Id] = node;
                    DeletedCount = Math.Max(0, DeletedCount - 1);
                }
            }
        });

        MarkDirty();
        Raise(VaultChangeKind.EntriesRemoved, entries, EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UndoAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_undo.Count == 0)
        {
            return Task.CompletedTask;
        }

        UndoStep step = _undo.Pop();
        step.Apply();
        _redo.Push(step);
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RedoAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ImportResult> ImportAsync(
        EntryId parent, IReadOnlyList<string> sourcePaths, ImportOptions options,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var imported = new List<EntryId>();
        long bytes = 0;

        await RunPhaseAsync(VaultOperation.Import, sourcePaths.Count * 4, progress, ct, (step, total) =>
        {
            int index = Math.Min(step / 4, Math.Max(sourcePaths.Count - 1, 0));
            return sourcePaths.Count == 0 ? "nothing to import" : System.IO.Path.GetFileName(sourcePaths[index]);
        }).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (string source in sourcePaths)
            {
                long length = 128 * 1024;
                Node node = Add(parent.Value, EntryKind.File, System.IO.Path.GetFileName(source), length, EntryState.Added);
                imported.Add(new EntryId(node.Id));
                bytes += length;
            }
        }

        MarkDirty();
        Raise(VaultChangeKind.EntriesAdded, imported, parent);
        return new ImportResult(imported, bytes, []);
    }

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<EntryId> entries, string destinationDirectory, ExportOptions options,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);

        await RunPhaseAsync(VaultOperation.Export, Math.Max(entries.Count, 1) * 4, progress, ct,
            (step, total) => "exporting").ConfigureAwait(false);

        return new ExportResult(entries.Count, 1, entries.Count * 128L * 1024, []);
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(EntryId file, CancellationToken ct)
    {
        EntryInfo? info = Find(file);
        int length = (int)Math.Min(info?.Length ?? 0, 4096);
        return Task.FromResult<Stream>(new MemoryStream(new byte[length]));
    }

    /// <inheritdoc />
    public async Task<VerifyReport> VerifyAsync(IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        VaultStatistics statistics = Statistics;
        var started = DateTimeOffset.UtcNow;

        await RunPhaseAsync(VaultOperation.Verify, 40, progress, ct, (step, total) => "authenticating blobs")
            .ConfigureAwait(false);

        return new VerifyReport(
            statistics.FileCount,
            statistics.TotalPlaintextBytes,
            DateTimeOffset.UtcNow - started,
            LayoutOk: true,
            Failures: []);
    }

    /// <inheritdoc />
    public async Task<ExportResult> RecoverAsync(
        string destinationDirectory, ExportOptions options, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        VaultStatistics statistics = Statistics;
        await RunPhaseAsync(VaultOperation.Recover, 40, progress, ct, (step, total) => "recovering").ConfigureAwait(false);
        return new ExportResult(statistics.FileCount, statistics.FolderCount, statistics.TotalPlaintextBytes, []);
    }

    /// <inheritdoc />
    public async Task SaveAsync(SaveOptions options, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        // A save of a real vault is the long one; the demo takes about three seconds so the
        // progress card, its ETA and the non-cancellable tail are all visible.
        await RunPhaseAsync(VaultOperation.Save, 90, progress, ct, (step, total) =>
            step < total - 12 ? "writing data section" : "swapping the file").ConfigureAwait(false);

        lock (_gate)
        {
            foreach (Node node in _nodes.Values)
            {
                node.State = EntryState.Stored;
            }

            DeletedCount = 0;
            CredentialChangePending = false;
            if (_rekeyPending)
            {
                _vaultIdHex = NewVaultIdHex();
                _rekeyPending = false;
            }

            _saveCounter++;
            _lastSaved = DateTimeOffset.UtcNow;
            _dirty = false;
        }

        _undo.Clear();
        _redo.Clear();
        Raise(VaultChangeKind.Saved, [], EntryId.Root);
        Raise(VaultChangeKind.DirtyChanged, [], EntryId.Root);
    }

    /// <inheritdoc />
    public Task SaveCopyAsync(
        string newPath, Passphrase password, KeyFile? keyFile, KdfParameters kdf, SaveOptions options,
        IProgress<VaultProgress>? progress, CancellationToken ct) =>
        RunPhaseAsync(VaultOperation.SaveCopy, 40, progress, ct, (step, total) => "writing the copy");

    /// <inheritdoc />
    public async Task ChangeCredentialsAsync(
        Passphrase newPassword, KeyFile? newKeyFile, KdfParameters kdf, CredentialChangeMode mode,
        IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kdf);

        await RunKeyDerivationAsync(kdf, progress, ct).ConfigureAwait(false);

        Kdf = kdf;
        CredentialChangePending = true;
        lock (_gate)
        {
            // A re-key installs a new vault key at the next save, and the vault id follows it.
            _rekeyPending = mode == CredentialChangeMode.Rekey;
        }

        MarkDirty();
        Raise(VaultChangeKind.EntryUpdated, [], EntryId.Root);
    }

    /// <inheritdoc />
    public Task DiscardChangesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _nodes.Clear();
            _nextId = 1;
            Seed();
            DeletedCount = 0;
            CredentialChangePending = false;
            _rekeyPending = false;
            _dirty = false;
        }

        _undo.Clear();
        _redo.Clear();
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Lock()
    {
        if (_locked)
        {
            return;
        }

        _locked = true;
        Raise(VaultChangeKind.LockChanged, [], EntryId.Root);
    }

    /// <inheritdoc />
    public async Task UnlockAsync(Passphrase password, KeyFile? keyFile, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        await RunKeyDerivationAsync(Kdf, progress, ct).ConfigureAwait(false);
        _locked = false;
        Raise(VaultChangeKind.LockChanged, [], EntryId.Root);
    }

    /// <inheritdoc />
    /// <remarks>The demo vault holds no key material, so every non-empty password is the right one.</remarks>
    public async Task<bool> VerifyPasswordAsync(Passphrase password, KeyFile? keyFile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        await RunKeyDerivationAsync(Kdf, null, ct).ConfigureAwait(false);
        return password.Length > 0;
    }

    /// <inheritdoc />
    public void ZeroKeys() => Lock();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Lock();
        return ValueTask.CompletedTask;
    }

    private static EntryInfo ToInfo(Node node) => new(
        new EntryId(node.Id),
        new EntryId(node.ParentId),
        node.Kind,
        node.Name,
        node.Length,
        node.ChildCount,
        node.Created,
        node.Modified,
        node.Comment,
        node.State);

    private async Task RunKeyDerivationAsync(KdfParameters kdf, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        // The KDF phase is not cancellable, and the demo makes that visible: the progress it
        // reports says so, and the delay is long enough to see the button change.
        progress?.Report(new VaultProgress(VaultOperation.KeyDerivation, 0, 0, 0, 1, "deriving key", false));
        await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new VaultProgress(VaultOperation.KeyDerivation, 0, 0, 1, 1, "deriving key", false));
    }

    private async Task RunPhaseAsync(
        VaultOperation operation, int steps, IProgress<VaultProgress>? progress, CancellationToken ct,
        Func<int, int, string> item)
    {
        _busy = true;
        try
        {
            long total = Math.Max(Statistics.TotalPlaintextBytes, 8L * 1024 * 1024);
            for (int step = 0; step <= steps; step++)
            {
                ct.ThrowIfCancellationRequested();

                bool cancellable = operation != VaultOperation.Save || step < steps - 12;
                long done = total * step / steps;
                progress?.Report(new VaultProgress(operation, done, total, step, steps, item(step, steps), cancellable));
                await Task.Delay(35, cancellable ? ct : CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;
        Raise(VaultChangeKind.DirtyChanged, [], EntryId.Root);
    }

    private void PushUndo(string description, Action apply)
    {
        _undo.Push(new UndoStep(description, apply));
        _redo.Clear();
    }

    private void Raise(VaultChangeKind kind, IReadOnlyList<EntryId> affected, EntryId parent) =>
        Changed?.Invoke(this, new VaultChangedEventArgs(kind, affected, parent));

    private Node Add(uint parent, EntryKind kind, string name, long length, EntryState state)
    {
        var node = new Node
        {
            Id = _nextId++,
            ParentId = parent,
            Kind = kind,
            Name = name,
            Length = length,
            Created = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 400)),
            Modified = DateTimeOffset.UtcNow.AddHours(-Random.Shared.Next(1, 900)),
            Comment = string.Empty,
            State = state,
        };

        _nodes[node.Id] = node;
        return node;
    }

    private void Remove(uint id)
    {
        lock (_gate)
        {
            _nodes.Remove(id);
        }
    }

    /// <summary>A fresh 16-byte vault id as 32 lowercase hex characters (FORMAT.md section 2.4).</summary>
    private static string NewVaultIdHex() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private void Seed()
    {
        Node documents = Add(0, EntryKind.Folder, "Documents", 0, EntryState.Stored);
        Node contracts = Add(documents.Id, EntryKind.Folder, "Contracts", 0, EntryState.Stored);
        Node invoices = Add(documents.Id, EntryKind.Folder, "Invoices", 0, EntryState.Stored);
        Node photos = Add(0, EntryKind.Folder, "Photos", 0, EntryState.Stored);
        Node trip = Add(photos.Id, EntryKind.Folder, "Trip 2025", 0, EntryState.Stored);
        Node keys = Add(0, EntryKind.Folder, "Keys and certificates", 0, EntryState.Stored);
        Node archive = Add(0, EntryKind.Folder, "Archive", 0, EntryState.Stored);

        AddFile(documents.Id, "Passport scan.pdf", 2_411_008, EntryState.Stored);
        AddFile(documents.Id, "Birth certificate.pdf", 1_204_224, EntryState.Stored);
        AddFile(documents.Id, "Insurance policy.pdf", 884_736, EntryState.Changed);
        AddFile(documents.Id, "Notes.txt", 4_096, EntryState.Stored);

        AddFile(contracts.Id, "Lease agreement 2024.pdf", 3_145_728, EntryState.Stored);
        AddFile(contracts.Id, "Employment contract.pdf", 1_572_864, EntryState.Stored);
        AddFile(contracts.Id, "NDA - Northwind.pdf", 655_360, EntryState.Added);
        AddFile(contracts.Id, "Amendment 3.docx", 98_304, EntryState.Stored);

        AddFile(invoices.Id, "2025-01 hosting.pdf", 122_880, EntryState.Stored);
        AddFile(invoices.Id, "2025-02 hosting.pdf", 124_928, EntryState.Stored);
        AddFile(invoices.Id, "2025-03 hosting.pdf", 121_856, EntryState.Stored);
        AddFile(invoices.Id, "2025-04 hosting.pdf", 126_976, EntryState.Added);
        AddFile(invoices.Id, "Summary.xlsx", 45_056, EntryState.Stored);

        AddFile(photos.Id, "Family portrait.jpg", 6_291_456, EntryState.Stored);
        AddFile(photos.Id, "House deed photo.jpg", 4_194_304, EntryState.Stored);
        AddFile(trip.Id, "IMG_0417.jpg", 5_242_880, EntryState.Stored);
        AddFile(trip.Id, "IMG_0418.jpg", 5_505_024, EntryState.Stored);
        AddFile(trip.Id, "IMG_0419.jpg", 4_980_736, EntryState.Stored);
        AddFile(trip.Id, "IMG_0420.jpg", 5_767_168, EntryState.Added);
        AddFile(trip.Id, "Itinerary.md", 8_192, EntryState.Stored);

        AddFile(keys.Id, "ssh_ed25519", 464, EntryState.Stored);
        AddFile(keys.Id, "ssh_ed25519.pub", 108, EntryState.Stored);
        AddFile(keys.Id, "signing.pfx", 4_096, EntryState.Stored);
        AddFile(keys.Id, "recovery-codes.txt", 512, EntryState.Changed);
        AddFile(keys.Id, "gpg-secret.asc", 3_584, EntryState.Stored);

        AddFile(archive.Id, "2019 tax return.zip", 12_582_912, EntryState.Stored);
        AddFile(archive.Id, "2020 tax return.zip", 13_631_488, EntryState.Stored);
        AddFile(archive.Id, "2021 tax return.zip", 11_534_336, EntryState.Stored);
        AddFile(archive.Id, "2022 tax return.zip", 14_680_064, EntryState.Stored);
        AddFile(archive.Id, "old-website-backup.tar", 47_185_920, EntryState.Stored);

        AddFile(0, "README.txt", 2_048, EntryState.Stored);
        AddFile(0, "Wallet backup.dat", 262_144, EntryState.Stored);
        AddFile(0, "Licence keys.md", 6_144, EntryState.Stored);
        AddFile(0, "Scan of ID card.png", 1_835_008, EntryState.Stored);
        AddFile(0, "Emergency contacts.csv", 3_072, EntryState.Stored);

        _dirty = _nodes.Values.Any(n => n.State != EntryState.Stored);
        RecomputeRollups();
    }

    private void AddFile(uint parent, string name, long length, EntryState state) =>
        Add(parent, EntryKind.File, name, length, state);

    private void RecomputeRollups()
    {
        foreach (Node folder in _nodes.Values.Where(n => n.Kind == EntryKind.Folder))
        {
            folder.ChildCount = _nodes.Values.Count(n => n.ParentId == folder.Id);
            folder.Length = _nodes.Values.Where(n => n.ParentId == folder.Id).Sum(n => n.Length);
        }
    }

    private sealed class Node
    {
        public uint Id { get; set; }

        public uint ParentId { get; set; }

        public EntryKind Kind { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Length { get; set; }

        public int ChildCount { get; set; }

        public DateTimeOffset Created { get; set; }

        public DateTimeOffset Modified { get; set; }

        public string Comment { get; set; } = string.Empty;

        public EntryState State { get; set; }
    }

    private sealed class UndoStep(string description, Action apply)
    {
        public string Description { get; } = description;

        public void Apply() => apply();
    }
}
