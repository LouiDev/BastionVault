using BastionVault.Core.Crypto;
using BastionVault.Core.Format;

namespace BastionVault.Core.Session;

/// <summary>
/// The one implementation of <see cref="IVaultSession"/>. It owns the in-memory tree, the staging
/// store, the undo journal and the save state machine of FORMAT.md section 8.3, and serialises every
/// long operation behind a single session lock.
/// </summary>
internal sealed partial class VaultSession : IVaultSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _treeGate = new();
    private readonly UndoStack _undo = new();
    private readonly HashSet<uint> _deletedStored = [];
    private readonly List<PendingCredentials> _credentialBin = [];
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly IKeyDerivation _kdf;
    private readonly IVaultPaths _paths;

    private VaultHeader _header;
    private VaultIndex _storedIndex;
    private StoredLayout _layout;
    private VaultCrypto? _crypto;
    private PendingCredentials? _pendingCredentials;
    private byte[] _vaultId;
    private FileStat _stat;
    private ulong _saveCounter;
    private DateTimeOffset? _lastSavedUtc;
    private bool _dirty;
    private bool _locked;
    private bool _disposed;

    /// <summary>Creates a session over an already opened, already authenticated vault file.</summary>
    /// <param name="path">Absolute path of the vault file.</param>
    /// <param name="file">The open file handle the session keeps for the whole session.</param>
    /// <param name="header">The parsed header.</param>
    /// <param name="index">The decrypted, validated index.</param>
    /// <param name="crypto">The unwrapped keys; the session takes ownership.</param>
    /// <param name="options">Open options.</param>
    /// <param name="readOnly">True when the session must refuse every mutation.</param>
    /// <param name="openedFromIndexCopy">True when the primary index failed and the copy was used.</param>
    /// <param name="stat">Length and last-write time captured right after opening.</param>
    /// <param name="random">Randomness seam.</param>
    /// <param name="clock">Time seam.</param>
    /// <param name="paths">Temp, backup and staging naming seam.</param>
    /// <param name="kdf">Key-derivation seam, used by unlock and credential changes.</param>
    internal VaultSession(
        string path,
        VaultFileHandle file,
        VaultHeader header,
        VaultIndex index,
        VaultCrypto crypto,
        OpenOptions options,
        bool readOnly,
        bool openedFromIndexCopy,
        FileStat stat,
        IRandomSource random,
        IClock clock,
        IVaultPaths paths,
        IKeyDerivation kdf)
    {
        Path = path;
        FileHandle = file;
        _header = header;
        _storedIndex = index;
        _crypto = crypto;
        IsReadOnly = readOnly;
        _paths = paths;
        _kdf = kdf;
        Random = random;
        Clock = clock;
        _stat = stat;
        _vaultId = (byte[])crypto.VaultId.Clone();
        _saveCounter = index.SaveCounter;
        _lastSavedUtc = index.SavedUtcTicks > 0 ? TreeModel.ToUtc(index.SavedUtcTicks) : null;
        OpenedFromIndexCopy = openedFromIndexCopy;

        Tree = BuildTree(index, file, header);
        _layout = CaptureLayout(index, header, stat.Length);
        Staging = new StagingStore(path, paths, options, _sessionId);
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
            lock (_treeGate)
            {
                return Convert.ToHexStringLower(_vaultId);
            }
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly { get; }

    /// <inheritdoc />
    public bool IsLocked
    {
        get
        {
            lock (_treeGate)
            {
                return _locked;
            }
        }
    }

    /// <inheritdoc />
    public bool IsDirty
    {
        get
        {
            lock (_treeGate)
            {
                return _dirty;
            }
        }
    }

    /// <inheritdoc />
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <inheritdoc />
    public KdfParameters Kdf
    {
        get
        {
            lock (_treeGate)
            {
                return _header.Kdf;
            }
        }
    }

    /// <inheritdoc />
    public VaultStatistics Statistics
    {
        get
        {
            lock (_treeGate)
            {
                return new VaultStatistics(
                    Tree.FolderCount,
                    Tree.FileCount,
                    Tree.TotalPlaintextBytes,
                    _stat.Length,
                    _saveCounter,
                    _lastSavedUtc,
                    OpenedFromIndexCopy);
            }
        }
    }

    /// <inheritdoc />
    public PendingChanges Pending
    {
        get
        {
            lock (_treeGate)
            {
                int added = 0;
                int changed = 0;
                long bytes = 0;
                bool rekey = _pendingCredentials?.Mode == CredentialChangeMode.Rekey;

                foreach (TreeNode node in Tree.CanonicalOrder())
                {
                    switch (node.State)
                    {
                        case EntryState.Added:
                            added++;
                            break;
                        case EntryState.Changed:
                            changed++;
                            break;
                        default:
                            break;
                    }

                    if (node.Content is { } content && (rekey || content.IsPending))
                    {
                        bytes += content.Length;
                    }
                }

                return new PendingChanges(added, changed, _deletedStored.Count, bytes, _pendingCredentials is not null, rekey);
            }
        }
    }

    /// <inheritdoc />
    public bool CanUndo
    {
        get
        {
            lock (_treeGate)
            {
                return _undo.CanUndo;
            }
        }
    }

    /// <inheritdoc />
    public bool CanRedo
    {
        get
        {
            lock (_treeGate)
            {
                return _undo.CanRedo;
            }
        }
    }

    /// <inheritdoc />
    public string? UndoDescription
    {
        get
        {
            lock (_treeGate)
            {
                return _undo.UndoDescription;
            }
        }
    }

    /// <inheritdoc />
    public string? RedoDescription
    {
        get
        {
            lock (_treeGate)
            {
                return _undo.RedoDescription;
            }
        }
    }

    /// <summary>The in-memory tree.</summary>
    internal TreeModel Tree { get; private set; }

    /// <summary>The staging store holding pending ciphertext.</summary>
    internal StagingStore Staging { get; }

    /// <summary>The open vault file.</summary>
    internal VaultFileHandle FileHandle { get; }

    /// <summary>Randomness seam.</summary>
    internal IRandomSource Random { get; }

    /// <summary>Time seam.</summary>
    internal IClock Clock { get; }

    /// <summary>True when the primary index failed to authenticate and the copy was used.</summary>
    internal bool OpenedFromIndexCopy { get; private set; }

    /// <summary>Rough length the vault file will have after the next save, used by the import pre-flight.</summary>
    internal long EstimatedVaultLength
    {
        get
        {
            lock (_treeGate)
            {
                return _stat.Length + Staging.StagedBytes;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> GetChildren(EntryId folder)
    {
        lock (_treeGate)
        {
            TreeNode? node = Tree.Find(folder);
            if (node is null || node.Kind != EntryKind.Folder)
            {
                return [];
            }

            List<TreeNode> children = TreeModel.OrderedChildren(node);
            var result = new List<EntryInfo>(children.Count);
            foreach (TreeNode child in children)
            {
                result.Add(Tree.Snapshot(child));
            }

            return result;
        }
    }

    /// <inheritdoc />
    public EntryInfo? Find(EntryId id)
    {
        lock (_treeGate)
        {
            TreeNode? node = Tree.Find(id);
            return node is null || node.Id == 0 ? null : Tree.Snapshot(node);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> GetAncestors(EntryId id)
    {
        lock (_treeGate)
        {
            TreeNode? node = Tree.Find(id);
            if (node is null || node.Id == 0)
            {
                return [];
            }

            var chain = new List<EntryInfo>();
            for (TreeNode? current = node; current is not null && current.Id != 0; current = current.Parent)
            {
                chain.Add(Tree.Snapshot(current));
            }

            chain.Reverse();
            return chain;
        }
    }

    /// <inheritdoc />
    public string FormatPath(EntryId id)
    {
        lock (_treeGate)
        {
            TreeNode? node = Tree.Find(id);
            return node is null ? VaultPath.Format([]) : TreeModel.FormatPath(node);
        }
    }

    /// <inheritdoc />
    public bool TryResolvePath(string vaultPath, out EntryId id)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);

        lock (_treeGate)
        {
            bool found = Tree.TryResolve(vaultPath, out TreeNode node);
            id = found ? new EntryId(node.Id) : EntryId.Root;
            return found;
        }
    }

    /// <inheritdoc />
    public NameCheck ValidateName(EntryId parent, string name, EntryId? ignoring = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (_treeGate)
        {
            TreeNode? folder = Tree.Find(parent);
            if (folder is null || folder.Kind != EntryKind.Folder)
            {
                return new NameCheck(false, "The destination folder does not exist.", null);
            }

            return TreeModel.ValidateName(folder, name, ignoring?.Value);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EntryInfo> Search(string nameSubstring, EntryId? scope, int maxResults, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nameSubstring);

        lock (_treeGate)
        {
            TreeNode? node = scope is null ? Tree.Root : Tree.Find(scope.Value);
            return node is null ? [] : Tree.Search(nameSubstring, node, maxResults, ct);
        }
    }

    /// <summary>Attaches a subtree and keeps the pending-deletion bookkeeping in step.</summary>
    /// <param name="node">Node to attach.</param>
    /// <param name="parent">Destination folder.</param>
    internal void AttachSubtree(TreeNode node, TreeNode parent)
    {
        lock (_treeGate)
        {
            Tree.Attach(node, parent);
            foreach (TreeNode member in TreeModel.Subtree(node))
            {
                _deletedStored.Remove(member.Id);
            }
        }
    }

    /// <summary>Detaches a subtree and remembers which stored entries a save must drop.</summary>
    /// <param name="node">Node to detach.</param>
    internal void DetachSubtree(TreeNode node)
    {
        lock (_treeGate)
        {
            foreach (TreeNode member in TreeModel.Subtree(node))
            {
                if (member.State != EntryState.Added)
                {
                    _deletedStored.Add(member.Id);
                }
            }

            Tree.Detach(node);
        }
    }

    /// <summary>Moves a node to another folder without touching the deletion bookkeeping.</summary>
    /// <param name="node">Node to move.</param>
    /// <param name="newParent">Destination folder.</param>
    internal void Reparent(TreeNode node, TreeNode newParent)
    {
        lock (_treeGate)
        {
            TreeNode? oldParent = node.Parent;
            oldParent?.Children.Remove(node);
            node.Parent = newParent;
            newParent.Children.Add(node);
            TreeModel.InvalidateRollups(oldParent);
            TreeModel.InvalidateRollups(newParent);
        }
    }

    /// <summary>Installs a pending credential change (used by undo and redo).</summary>
    /// <param name="credentials">The change to make current, or <see langword="null"/> for none.</param>
    internal void SetPendingCredentials(PendingCredentials? credentials)
    {
        lock (_treeGate)
        {
            _pendingCredentials = credentials;
        }
    }

    /// <summary>Opens a reader over the content of an entry with the session keys.</summary>
    /// <param name="content">The content reference.</param>
    /// <param name="vaultPath">In-vault path used in integrity errors.</param>
    internal BlobReader OpenBlobReader(BlobRef content, string vaultPath) =>
        new(content.Source, RequireCrypto(), content.BlobId, content.Length, content.ChunkSize, vaultPath);

    /// <summary>The current keys.</summary>
    /// <exception cref="VaultOperationException">The session is locked.</exception>
    internal VaultCrypto RequireCrypto() =>
        _crypto ?? throw new VaultOperationException(
            VaultErrorCode.SessionLocked,
            "The session is locked; unlock it before running this operation.");

    /// <summary>Eight hex characters for a temporary file name.</summary>
    internal string NewSuffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        Random.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Builds the in-memory tree from a decrypted index.</summary>
    /// <param name="index">The index.</param>
    /// <param name="file">The open vault file.</param>
    /// <param name="header">The header, for the data section offset.</param>
    private static TreeModel BuildTree(VaultIndex index, VaultFileHandle file, VaultHeader header)
    {
        var tree = new TreeModel { NextEntryId = index.NextEntryId };
        var byId = new Dictionary<uint, TreeNode> { [0] = tree.Root };

        foreach (IndexEntry entry in index.Entries)
        {
            var node = new TreeNode
            {
                Id = entry.Id,
                Kind = entry.Kind,
                Name = entry.Name,
                Comment = entry.Comment,
                CreatedUtcTicks = entry.CreatedUtcTicks,
                ModifiedUtcTicks = entry.ModifiedUtcTicks,
                State = EntryState.Stored,
            };

            if (entry.Kind == EntryKind.File)
            {
                long blobLength = ChunkCipher.BlobLength(entry.Length, entry.ChunkSize);
                node.Content = new BlobRef
                {
                    BlobId = entry.BlobId!,
                    Source = new StoredBlobSource(file, header.DataSectionOffset + entry.DataOffset, blobLength),
                    Length = entry.Length,
                    ChunkSize = entry.ChunkSize,
                    BlobHash = entry.BlobHash!,
                };
            }

            // The index is validated parent-first, so the parent is always already known.
            TreeNode parent = byId[entry.ParentId];
            tree.Attach(node, parent);
            byId[entry.Id] = node;
        }

        if (tree.NextEntryId == 0)
        {
            tree.NextEntryId = 1;
        }

        return tree;
    }

    /// <summary>Captures what <see cref="VerifyAsync"/> needs to check the on-disk layout.</summary>
    /// <param name="index">The index that describes the file.</param>
    /// <param name="header">The header that describes the file.</param>
    /// <param name="fileLength">Length of the file.</param>
    private static StoredLayout CaptureLayout(VaultIndex index, VaultHeader header, long fileLength)
    {
        var blobs = new List<(long Offset, long Length)>();
        foreach (IndexEntry entry in index.Entries)
        {
            if (entry.Kind == EntryKind.File)
            {
                blobs.Add((entry.DataOffset, ChunkCipher.BlobLength(entry.Length, entry.ChunkSize)));
            }
        }

        return new StoredLayout(header.IndexLength, index.DataSectionLength, index.DataPaddingLength, blobs, fileLength);
    }

    /// <summary>Takes the session lock or reports that the session is busy.</summary>
    /// <exception cref="VaultOperationException"><see cref="VaultErrorCode.Busy"/>.</exception>
    private GateScope EnterGate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_gate.Wait(0))
        {
            throw new VaultOperationException(
                VaultErrorCode.Busy,
                "Another operation is already running on this vault; calls are never queued.");
        }

        return new GateScope(this);
    }

    /// <summary>Refuses the call while the session is locked.</summary>
    private void RequireUnlocked()
    {
        if (IsLocked)
        {
            throw new VaultOperationException(
                VaultErrorCode.SessionLocked,
                "The session is locked; unlock it before running this operation.");
        }
    }

    /// <summary>Refuses the call on a read-only session.</summary>
    private void RequireWritable()
    {
        if (IsReadOnly)
        {
            throw new VaultOperationException(
                VaultErrorCode.ReadOnlySession,
                "This vault was opened read-only; it cannot be changed.");
        }
    }

    /// <summary>Returns the node behind an id, or throws the argument error the API prescribes.</summary>
    /// <param name="id">The id.</param>
    /// <param name="parameterName">Name of the parameter that carried it.</param>
    private TreeNode RequireNode(EntryId id, string parameterName) =>
        Tree.Find(id) ?? throw new ArgumentException($"There is no entry with id {id.Value} in this vault.", parameterName);

    /// <summary>Returns the folder behind an id, or throws the argument error the API prescribes.</summary>
    /// <param name="id">The id.</param>
    /// <param name="parameterName">Name of the parameter that carried it.</param>
    private TreeNode RequireFolder(EntryId id, string parameterName)
    {
        TreeNode node = RequireNode(id, parameterName);
        return node.Kind == EntryKind.Folder
            ? node
            : throw new ArgumentException($"Entry {id.Value} is a file, not a folder.", parameterName);
    }

    /// <summary>Marks the session dirty and raises the transition when it flips.</summary>
    private void MarkDirty()
    {
        bool flipped;
        lock (_treeGate)
        {
            flipped = !_dirty;
            _dirty = true;
        }

        if (flipped)
        {
            Raise(VaultChangeKind.DirtyChanged, [], EntryId.Root);
        }
    }

    /// <summary>Clears the dirty flag and raises the transition when it flips.</summary>
    private void ClearDirty()
    {
        bool flipped;
        lock (_treeGate)
        {
            flipped = _dirty;
            _dirty = false;
        }

        if (flipped)
        {
            Raise(VaultChangeKind.DirtyChanged, [], EntryId.Root);
        }
    }

    /// <summary>Raises <see cref="Changed"/> on the calling thread.</summary>
    /// <param name="kind">What happened.</param>
    /// <param name="affected">Ids affected.</param>
    /// <param name="parent">Parent the change relates to.</param>
    private void Raise(VaultChangeKind kind, IReadOnlyList<EntryId> affected, EntryId parent)
    {
        Changed?.Invoke(this, new VaultChangedEventArgs(kind, affected, parent));
    }

    /// <summary>Releases the session lock when the operation ends.</summary>
    private readonly struct GateScope : IDisposable
    {
        private readonly VaultSession _session;

        /// <summary>Remembers the session whose lock is held.</summary>
        /// <param name="session">The session.</param>
        public GateScope(VaultSession session) => _session = session;

        /// <inheritdoc />
        public void Dispose() => _session._gate.Release();
    }
}
