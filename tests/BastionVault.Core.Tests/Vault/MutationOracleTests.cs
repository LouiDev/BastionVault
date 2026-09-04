namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// Three hundred random sequences of tree edits, every step compared against an in-memory model of what
/// the tree must look like. The model knows nothing about how the session implements undo: it keeps a
/// stack of whole snapshots, so a step-based journal that forgets to restore a parent, a name, a
/// comment or a cached folder size is caught the moment the two disagree.
/// </summary>
public sealed class MutationOracleTests
{
    /// <summary>Number of random sequences.</summary>
    private const int Sequences = 300;

    /// <summary>Largest number of operations in one sequence.</summary>
    private const int MaxSequenceLength = 12;

    /// <summary>Above this many entries the generator turns new folders into deletions.</summary>
    private const int SoftEntryLimit = 60;

    private readonly Dictionary<string, int> _applied = [];
    private int _counter;

    [Fact]
    public async Task Random_mutation_sequences_agree_with_an_in_memory_model()
    {
        using var work = new TempDirectory("oracle");
        string path = Path.Combine(work.Path, "oracle.bastion");
        string sources = work.SubDirectory("source");

        using Passphrase password = Passphrase.FromString(TamperVault.Password);
        var factory = new VaultFactory(new DeterministicRandomSource(6), new FixedClock(GoldenVault.Epoch));
        Oracle oracle;

        await using (IVaultSession session = await factory.CreateAsync(
            path, password, null, GoldenVault.Kdf, null, CancellationToken.None))
        {
            await SeedContentAsync(session, sources);

            // A save clears the undo journal, so the sequences start from a known, quiet state.
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            oracle = Oracle.From(session);
            var random = new Random(20260201);

            for (int sequence = 0; sequence < Sequences; sequence++)
            {
                int length = random.Next(1, MaxSequenceLength + 1);
                for (int step = 0; step < length; step++)
                {
                    string what = await ApplyAsync(session, oracle, random);
                    AssertAgrees(session, oracle, $"sequence {sequence}, step {step} ({what})");
                }
            }

            // A generator that quietly stopped producing one kind of edit would still pass every
            // assertion above, so the mix itself is part of the contract of this test.
            foreach ((string kind, int count) in _applied.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                Assert.True(count >= 40, $"only {count} of the {Sequences} sequences ever did a {kind}");
            }

            Assert.Equal(8, _applied.Count);
            Assert.True(oracle.Nodes.Count > 5, "the sequences left an almost empty vault behind");

            // Whatever the sequences ended with has to survive the round trip through the format.
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
            AssertAgrees(session, oracle, "after the final save", journalCleared: true);
        }

        await using IVaultSession reopened = await new VaultFactory(
                new DeterministicRandomSource(7), new FixedClock(GoldenVault.Epoch))
            .OpenAsync(path, password, null, OpenOptions.Default, null, CancellationToken.None);

        AssertAgrees(reopened, oracle, "after reopening the saved vault", journalCleared: true);
        Assert.True((await reopened.VerifyAsync(null, CancellationToken.None)).IsClean);
    }

    /// <summary>Imports a handful of files of different sizes so folder rollups have something to add up.</summary>
    /// <param name="session">Session to fill.</param>
    /// <param name="sources">Directory the sources are written to.</param>
    private static async Task SeedContentAsync(IVaultSession session, string sources)
    {
        var options = new ImportOptions(PreserveTimestamps: false);
        int[] lengths = [0, 1, 7, 100, 1024, 4096];

        for (int i = 0; i < lengths.Length; i++)
        {
            byte[] content = new byte[lengths[i]];
            new DeterministicRandomSource((ulong)i + 1).Fill(content);
            string source = Path.Combine(sources, $"seed{i}.bin");
            await File.WriteAllBytesAsync(source, content).ConfigureAwait(false);
            await session.ImportAsync(EntryId.Root, [source], options, null, CancellationToken.None).ConfigureAwait(false);
        }

        EntryId folder = await session
            .CreateFolderAsync(EntryId.Root, "seeded folder", CancellationToken.None)
            .ConfigureAwait(false);

        await session.MoveAsync(
            [session.GetChildren(EntryId.Root).First(entry => entry.Name == "seed3.bin").Id],
            folder,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Applies one random operation to both the session and the model.</summary>
    /// <param name="session">Session under test.</param>
    /// <param name="oracle">The model.</param>
    /// <param name="random">Random source.</param>
    /// <returns>A description of what was done, for assertion messages.</returns>
    private async Task<string> ApplyAsync(IVaultSession session, Oracle oracle, Random random)
    {
        List<uint> entries = [.. oracle.Nodes.Keys];
        List<uint> folders =
        [
            0,
            .. oracle.Nodes.Values.Where(node => node.Kind == EntryKind.Folder).Select(node => node.Id),
        ];

        int choice = random.Next(10);
        if (choice is 0 or 1 && oracle.Nodes.Count > SoftEntryLimit)
        {
            // Keep the tree in a size where a full comparison after every single step stays cheap.
            choice = 5;
        }

        if (entries.Count == 0 && choice is 2 or 3 or 4 or 5 or 6)
        {
            choice = 0;
        }

        switch (choice)
        {
            case 0:
            case 1:
            {
                uint parent = folders[random.Next(folders.Count)];
                string name = NextName();
                EntryId created = await session
                    .CreateFolderAsync(new EntryId(parent), name, CancellationToken.None)
                    .ConfigureAwait(false);

                oracle.Begin();
                oracle.Add(created.Value, parent, name);
                return Record("create", $"create folder {created.Value} under {parent}");
            }

            case 2:
            case 3:
            {
                uint id = entries[random.Next(entries.Count)];
                string name = NextName();
                await session.RenameAsync(new EntryId(id), name, CancellationToken.None).ConfigureAwait(false);

                oracle.Begin();
                oracle.Rename(id, name);
                return Record("rename", $"rename {id}");
            }

            case 4:
            {
                (uint Id, uint Target)? move = PickMove(oracle, random);
                if (move is null)
                {
                    return await InvalidAsync(session, oracle).ConfigureAwait(false);
                }

                await session
                    .MoveAsync([new EntryId(move.Value.Id)], new EntryId(move.Value.Target), CancellationToken.None)
                    .ConfigureAwait(false);

                oracle.Begin();
                oracle.Move(move.Value.Id, move.Value.Target);
                return Record("move", $"move {move.Value.Id} into {move.Value.Target}");
            }

            case 5:
            {
                uint id = entries[random.Next(entries.Count)];
                await session.DeleteAsync([new EntryId(id)], CancellationToken.None).ConfigureAwait(false);

                oracle.Begin();
                oracle.Remove(id);
                return Record("delete", $"delete {id}");
            }

            case 6:
            {
                uint id = entries[random.Next(entries.Count)];
                string comment = $"comment {++_counter}";
                await session.SetCommentAsync(new EntryId(id), comment, CancellationToken.None).ConfigureAwait(false);

                oracle.Begin();
                oracle.SetComment(id, comment);
                return Record("comment", $"comment {id}");
            }

            case 7:
            {
                Assert.Equal(oracle.CanUndo, session.CanUndo);
                await session.UndoAsync(CancellationToken.None).ConfigureAwait(false);
                if (oracle.CanUndo)
                {
                    oracle.Undo();
                }

                return Record("undo", "undo");
            }

            case 8:
            {
                Assert.Equal(oracle.CanRedo, session.CanRedo);
                await session.RedoAsync(CancellationToken.None).ConfigureAwait(false);
                if (oracle.CanRedo)
                {
                    oracle.Redo();
                }

                return Record("redo", "redo");
            }

            default:
                return await InvalidAsync(session, oracle).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs an operation the API must refuse and asserts that it changes neither the tree nor the
    /// journal: a rejected edit is never half applied. The model is deliberately not told about it.
    /// </summary>
    /// <param name="session">Session under test.</param>
    /// <param name="oracle">The model, used only to find a suitable victim.</param>
    private async Task<string> InvalidAsync(IVaultSession session, Oracle oracle)
    {
        OracleNode? nested = oracle.Nodes.Values.FirstOrDefault(
            node => node.Kind == EntryKind.Folder && node.ParentId != 0);
        OracleNode? any = oracle.Nodes.Values.FirstOrDefault();
        OracleNode? sibling = any is null
            ? null
            : oracle.Nodes.Values.FirstOrDefault(node => node.Id != any.Id && node.ParentId == any.ParentId);

        switch (++_counter % 4)
        {
            case 0:
            {
                VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                    () => session.CreateFolderAsync(EntryId.Root, "bad:name", CancellationToken.None));
                VaultAssert.Failure(error, VaultErrorCode.NameInvalid, "a name with a colon");
                return Record("rejection", "rejected an invalid name");
            }

            case 1:
            {
                VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                    () => session.DeleteAsync([EntryId.Root], CancellationToken.None));
                VaultAssert.Failure(error, VaultErrorCode.InvalidMove, "deleting the root");
                return Record("rejection", "rejected deleting the root");
            }

            case 2 when nested is not null:
            {
                VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                    () => session.MoveAsync([new EntryId(nested.ParentId)], new EntryId(nested.Id), CancellationToken.None));
                VaultAssert.Failure(error, VaultErrorCode.InvalidMove, "moving a folder into its own child");
                return Record("rejection", "rejected a move into a descendant");
            }

            case 3 when any is not null && sibling is not null:
            {
                VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                    () => session.RenameAsync(new EntryId(any.Id), sibling.Name, CancellationToken.None));
                VaultAssert.Failure(error, VaultErrorCode.NameConflict, "renaming onto a sibling");
                return Record("rejection", "rejected a name conflict");
            }

            default:
            {
                VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
                    () => session.CreateFolderAsync(EntryId.Root, "trailing space ", CancellationToken.None));
                VaultAssert.Failure(error, VaultErrorCode.NameInvalid, "a name with a trailing space");
                return Record("rejection", "rejected a trailing space");
            }
        }
    }

    /// <summary>Picks a move the API must accept, or nothing when the tree offers none.</summary>
    /// <param name="oracle">The model.</param>
    /// <param name="random">Random source.</param>
    private static (uint Id, uint Target)? PickMove(Oracle oracle, Random random)
    {
        List<uint> entries = [.. oracle.Nodes.Keys];
        if (entries.Count == 0)
        {
            return null;
        }

        for (int attempt = 0; attempt < 16; attempt++)
        {
            uint id = entries[random.Next(entries.Count)];
            List<uint> targets =
            [
                0,
                .. oracle.Nodes.Values
                    .Where(node => node.Kind == EntryKind.Folder && !oracle.IsSelfOrDescendantOf(node.Id, id))
                    .Select(node => node.Id),
            ];

            uint target = targets[random.Next(targets.Count)];
            if (target != oracle.Nodes[id].ParentId)
            {
                return (id, target);
            }
        }

        return null;
    }

    /// <summary>A name that has never been used before, so no generated operation can ever collide.</summary>
    private string NextName() => $"entry {++_counter}";

    /// <summary>Counts one applied operation so the test can prove the generator stayed varied.</summary>
    /// <param name="kind">Kind of operation.</param>
    /// <param name="detail">Description of the operation, passed through unchanged.</param>
    private string Record(string kind, string detail)
    {
        _applied[kind] = _applied.GetValueOrDefault(kind) + 1;
        return detail;
    }

    /// <summary>Asserts that the session and the model describe exactly the same tree.</summary>
    /// <param name="session">Session under test.</param>
    /// <param name="oracle">The model.</param>
    /// <param name="because">Description of the step, shown when the assertion fails.</param>
    /// <param name="journalCleared">True when a save has just dropped the undo journal.</param>
    private static void AssertAgrees(IVaultSession session, Oracle oracle, string because, bool journalCleared = false)
    {
        IReadOnlyDictionary<uint, Expectation> expected = oracle.Describe();
        var seen = new HashSet<uint>();
        Walk(session, EntryId.Root, expected, seen, because);

        Assert.True(
            seen.SetEquals(expected.Keys),
            $"{because}: the session holds [{string.Join(", ", seen.Order())}] " +
            $"but the model expects [{string.Join(", ", expected.Keys.Order())}]");

        VaultStatistics statistics = session.Statistics;
        Assert.True(
            statistics.FileCount == expected.Values.Count(entry => entry.Kind == EntryKind.File),
            $"{because}: file count");
        Assert.True(
            statistics.FolderCount == expected.Values.Count(entry => entry.Kind == EntryKind.Folder),
            $"{because}: folder count");

        Assert.True(session.CanUndo == (!journalCleared && oracle.CanUndo), $"{because}: CanUndo");
        Assert.True(session.CanRedo == (!journalCleared && oracle.CanRedo), $"{because}: CanRedo");
    }

    /// <summary>Walks the session tree and compares every entry with the model.</summary>
    /// <param name="session">Session under test.</param>
    /// <param name="folder">Folder to descend into.</param>
    /// <param name="expected">What the model says every entry must look like.</param>
    /// <param name="seen">Set that collects every id the session exposes.</param>
    /// <param name="because">Description of the step, shown when the assertion fails.</param>
    private static void Walk(
        IVaultSession session,
        EntryId folder,
        IReadOnlyDictionary<uint, Expectation> expected,
        HashSet<uint> seen,
        string because)
    {
        foreach (EntryInfo info in session.GetChildren(folder))
        {
            uint id = info.Id.Value;
            Assert.True(seen.Add(id), $"{because}: entry {id} appears twice");
            Assert.True(expected.TryGetValue(id, out Expectation? entry), $"{because}: entry {id} is unexpected");

            Assert.True(entry!.Name == info.Name, $"{because}: entry {id} is called {info.Name}, expected {entry.Name}");
            Assert.True(entry.ParentId == info.ParentId.Value, $"{because}: entry {id} has the wrong parent");
            Assert.True(entry.Kind == info.Kind, $"{because}: entry {id} has the wrong kind");
            Assert.True(entry.Comment == info.Comment, $"{because}: entry {id} has the wrong comment");
            Assert.True(entry.Size == info.Length, $"{because}: entry {id} reports {info.Length} bytes, expected {entry.Size}");
            Assert.True(entry.ChildCount == info.ChildCount, $"{because}: entry {id} has the wrong child count");
            Assert.True(entry.Path == session.FormatPath(info.Id), $"{because}: entry {id} has the wrong path");

            if (info.Kind == EntryKind.Folder)
            {
                Walk(session, info.Id, expected, seen, because);
            }
        }
    }

    /// <summary>One entry of the model.</summary>
    /// <param name="Id">Entry id.</param>
    /// <param name="ParentId">Parent id; 0 for a top-level entry.</param>
    /// <param name="Kind">Folder or file.</param>
    /// <param name="Name">Entry name.</param>
    /// <param name="Comment">Entry comment.</param>
    /// <param name="Length">Plaintext length of a file; 0 for a folder.</param>
    private sealed record OracleNode(uint Id, uint ParentId, EntryKind Kind, string Name, string Comment, long Length);

    /// <summary>Everything an <see cref="EntryInfo"/> must report for one entry.</summary>
    /// <param name="ParentId">Parent id.</param>
    /// <param name="Kind">Folder or file.</param>
    /// <param name="Name">Entry name.</param>
    /// <param name="Comment">Entry comment.</param>
    /// <param name="Size">Plaintext length of a file, recursive rollup of a folder.</param>
    /// <param name="ChildCount">Direct children of a folder; 0 for a file.</param>
    /// <param name="Path">In-vault path.</param>
    private sealed record Expectation(
        uint ParentId, EntryKind Kind, string Name, string Comment, long Size, int ChildCount, string Path);

    /// <summary>
    /// The model: the tree as a flat map plus a snapshot-based undo journal. It shares no code with the
    /// session implementation, which is the whole point of comparing against it.
    /// </summary>
    private sealed class Oracle
    {
        private readonly Stack<Dictionary<uint, OracleNode>> _undo = new();
        private readonly Stack<Dictionary<uint, OracleNode>> _redo = new();
        private Dictionary<uint, OracleNode> _nodes = [];

        /// <summary>Every entry of the modelled tree, by id.</summary>
        public IReadOnlyDictionary<uint, OracleNode> Nodes => _nodes;

        /// <summary>True when the journal holds a step to revert.</summary>
        public bool CanUndo => _undo.Count > 0;

        /// <summary>True when the journal holds a reverted step to apply again.</summary>
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Builds a model from the current contents of a session.</summary>
        /// <param name="session">Session to copy.</param>
        public static Oracle From(IVaultSession session)
        {
            var oracle = new Oracle();
            Collect(session, EntryId.Root, oracle._nodes);
            return oracle;
        }

        /// <summary>Records the state before a mutation and drops the redo branch.</summary>
        public void Begin()
        {
            _undo.Push(new Dictionary<uint, OracleNode>(_nodes));
            _redo.Clear();
        }

        /// <summary>Reverts to the state before the newest recorded mutation.</summary>
        public void Undo()
        {
            _redo.Push(_nodes);
            _nodes = _undo.Pop();
        }

        /// <summary>Applies the newest reverted state again.</summary>
        public void Redo()
        {
            _undo.Push(_nodes);
            _nodes = _redo.Pop();
        }

        /// <summary>Adds a folder.</summary>
        /// <param name="id">Entry id.</param>
        /// <param name="parentId">Parent id.</param>
        /// <param name="name">Entry name.</param>
        public void Add(uint id, uint parentId, string name) =>
            _nodes[id] = new OracleNode(id, parentId, EntryKind.Folder, name, string.Empty, 0);

        /// <summary>Renames an entry.</summary>
        /// <param name="id">Entry id.</param>
        /// <param name="name">New name.</param>
        public void Rename(uint id, string name) => _nodes[id] = _nodes[id] with { Name = name };

        /// <summary>Sets the comment of an entry.</summary>
        /// <param name="id">Entry id.</param>
        /// <param name="comment">New comment.</param>
        public void SetComment(uint id, string comment) => _nodes[id] = _nodes[id] with { Comment = comment };

        /// <summary>Moves an entry into another folder.</summary>
        /// <param name="id">Entry id.</param>
        /// <param name="parentId">Destination folder id.</param>
        public void Move(uint id, uint parentId) => _nodes[id] = _nodes[id] with { ParentId = parentId };

        /// <summary>Removes an entry and everything below it.</summary>
        /// <param name="id">Entry id.</param>
        public void Remove(uint id)
        {
            foreach (uint child in _nodes.Values.Where(node => node.ParentId == id).Select(node => node.Id).ToList())
            {
                Remove(child);
            }

            _nodes.Remove(id);
        }

        /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or sits below it.</summary>
        /// <param name="candidate">Entry to test.</param>
        /// <param name="ancestor">Possible ancestor; never the root.</param>
        public bool IsSelfOrDescendantOf(uint candidate, uint ancestor)
        {
            for (uint current = candidate; current != 0; current = _nodes[current].ParentId)
            {
                if (current == ancestor)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Computes, in one pass, everything the session must report for every entry.</summary>
        public IReadOnlyDictionary<uint, Expectation> Describe()
        {
            var children = new Dictionary<uint, List<OracleNode>>();
            foreach (OracleNode node in _nodes.Values)
            {
                if (!children.TryGetValue(node.ParentId, out List<OracleNode>? list))
                {
                    list = [];
                    children[node.ParentId] = list;
                }

                list.Add(node);
            }

            var result = new Dictionary<uint, Expectation>(_nodes.Count);
            foreach (OracleNode root in children.GetValueOrDefault(0u) ?? [])
            {
                Visit(root, string.Empty, children, result);
            }

            return result;
        }

        /// <summary>Copies a session subtree into the model.</summary>
        /// <param name="session">Session to copy.</param>
        /// <param name="folder">Folder to descend into.</param>
        /// <param name="nodes">Map that receives the entries.</param>
        private static void Collect(IVaultSession session, EntryId folder, Dictionary<uint, OracleNode> nodes)
        {
            foreach (EntryInfo info in session.GetChildren(folder))
            {
                nodes[info.Id.Value] = new OracleNode(
                    info.Id.Value,
                    info.ParentId.Value,
                    info.Kind,
                    info.Name,
                    info.Comment,
                    info.Kind == EntryKind.File ? info.Length : 0);

                if (info.Kind == EntryKind.Folder)
                {
                    Collect(session, info.Id, nodes);
                }
            }
        }

        /// <summary>Describes one subtree and returns its recursive plaintext size.</summary>
        /// <param name="node">Root of the subtree.</param>
        /// <param name="parentPath">Path of the parent folder.</param>
        /// <param name="children">Children by parent id.</param>
        /// <param name="result">Map that receives the expectations.</param>
        private static long Visit(
            OracleNode node,
            string parentPath,
            Dictionary<uint, List<OracleNode>> children,
            Dictionary<uint, Expectation> result)
        {
            string path = parentPath + "\\" + node.Name;
            List<OracleNode> kids = children.GetValueOrDefault(node.Id) ?? [];

            long size = 0;
            foreach (OracleNode child in kids)
            {
                size += Visit(child, path, children, result);
            }

            if (node.Kind == EntryKind.File)
            {
                size = node.Length;
            }

            result[node.Id] = new Expectation(
                node.ParentId,
                node.Kind,
                node.Name,
                node.Comment,
                size,
                node.Kind == EntryKind.Folder ? kids.Count : 0,
                path);

            return size;
        }
    }
}
