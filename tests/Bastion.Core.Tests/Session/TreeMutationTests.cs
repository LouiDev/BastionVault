namespace Bastion.Core.Tests.Session;

/// <summary>Folder creation, renaming, moving, copying, deleting and the undo journal.</summary>
public sealed class TreeMutationTests
{
    [Fact]
    public async Task Creating_a_folder_raises_a_change_and_marks_the_session_dirty()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        var events = new List<VaultChangedEventArgs>();
        session.Changed += (_, args) => events.Add(args);

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);

        EntryInfo? info = session.Find(folder);
        Assert.NotNull(info);
        Assert.Equal("Documents", info.Name);
        Assert.Equal(EntryKind.Folder, info.Kind);
        Assert.Equal(EntryState.Added, info.State);
        Assert.Equal(context.Clock.UtcNow, info.CreatedUtc);
        Assert.Contains(events, args => args.Kind == VaultChangeKind.EntriesAdded);
        Assert.Contains(events, args => args.Kind == VaultChangeKind.DirtyChanged);
    }

    [Fact]
    public async Task Creating_a_folder_rejects_an_invalid_or_duplicate_name()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);

        VaultOperationException duplicate = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.CreateFolderAsync(EntryId.Root, "DOCUMENTS", CancellationToken.None));
        Assert.Equal(VaultErrorCode.NameConflict, duplicate.Code);

        VaultOperationException invalid = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.CreateFolderAsync(EntryId.Root, "bad|name", CancellationToken.None));
        Assert.Equal(VaultErrorCode.NameInvalid, invalid.Code);

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.CreateFolderAsync(new EntryId(9999), "Whatever", CancellationToken.None));
    }

    [Fact]
    public async Task Rename_and_undo_and_redo_restore_the_name_and_the_state()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        Assert.Equal(EntryState.Stored, session.Find(folder)!.State);

        await session.RenameAsync(folder, "Papers", CancellationToken.None);
        Assert.Equal("Papers", session.Find(folder)!.Name);
        Assert.Equal(EntryState.Changed, session.Find(folder)!.State);
        Assert.Equal("Rename Documents to Papers", session.UndoDescription);

        await session.UndoAsync(CancellationToken.None);
        Assert.Equal("Documents", session.Find(folder)!.Name);
        Assert.Equal(EntryState.Stored, session.Find(folder)!.State);
        Assert.False(session.IsDirty);
        Assert.True(session.CanRedo);

        await session.RedoAsync(CancellationToken.None);
        Assert.Equal("Papers", session.Find(folder)!.Name);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public async Task Comments_round_trip_through_a_save()
    {
        using var context = new VaultTestContext();
        EntryId folder;

        await using (IVaultSession session = await context.CreateAsync())
        {
            folder = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
            await session.SetCommentAsync(folder, "Tax papers\tand receipts", CancellationToken.None);
            Assert.Equal("Tax papers\tand receipts", session.Find(folder)!.Comment);

            VaultOperationException tooLong = await Assert.ThrowsAsync<VaultOperationException>(
                () => session.SetCommentAsync(folder, new string('x', 5000), CancellationToken.None));
            Assert.Equal(VaultErrorCode.NameInvalid, tooLong.Code);

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal("Tax papers\tand receipts", reopened.Find(folder)!.Comment);
    }

    [Fact]
    public async Task Move_rejects_a_move_into_a_descendant_and_keeps_ids()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId outer = await session.CreateFolderAsync(EntryId.Root, "Outer", CancellationToken.None);
        EntryId inner = await session.CreateFolderAsync(outer, "Inner", CancellationToken.None);
        EntryId other = await session.CreateFolderAsync(EntryId.Root, "Other", CancellationToken.None);

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.MoveAsync([outer], inner, CancellationToken.None));
        Assert.Equal(VaultErrorCode.InvalidMove, error.Code);

        await session.MoveAsync([inner], other, CancellationToken.None);
        Assert.Equal(other, session.Find(inner)!.ParentId);
        Assert.Equal("\\Other\\Inner", session.FormatPath(inner));

        await session.UndoAsync(CancellationToken.None);
        Assert.Equal(outer, session.Find(inner)!.ParentId);
    }

    [Fact]
    public async Task Move_reports_a_name_conflict_at_the_destination()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId source = await session.CreateFolderAsync(EntryId.Root, "Source", CancellationToken.None);
        EntryId target = await session.CreateFolderAsync(EntryId.Root, "Target", CancellationToken.None);
        EntryId moving = await session.CreateFolderAsync(source, "Same", CancellationToken.None);
        await session.CreateFolderAsync(target, "same", CancellationToken.None);

        VaultOperationException error = await Assert.ThrowsAsync<VaultOperationException>(
            () => session.MoveAsync([moving], target, CancellationToken.None));
        Assert.Equal(VaultErrorCode.NameConflict, error.Code);
    }

    [Fact]
    public async Task Delete_and_undo_restore_the_whole_subtree()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId outer = await session.CreateFolderAsync(EntryId.Root, "Outer", CancellationToken.None);
        EntryId inner = await session.CreateFolderAsync(outer, "Inner", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await session.DeleteAsync([outer], CancellationToken.None);
        Assert.Null(session.Find(outer));
        Assert.Null(session.Find(inner));
        Assert.Equal(2, session.Pending.Deleted);
        Assert.Equal("Delete Outer", session.UndoDescription);

        await session.UndoAsync(CancellationToken.None);
        Assert.NotNull(session.Find(outer));
        Assert.NotNull(session.Find(inner));
        Assert.Equal(0, session.Pending.Deleted);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Deleting_a_folder_and_its_child_together_is_not_counted_twice()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId outer = await session.CreateFolderAsync(EntryId.Root, "Outer", CancellationToken.None);
        EntryId inner = await session.CreateFolderAsync(outer, "Inner", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await session.DeleteAsync([outer, inner], CancellationToken.None);
        Assert.Equal(2, session.Pending.Deleted);

        await session.UndoAsync(CancellationToken.None);
        Assert.NotNull(session.Find(inner));
    }

    [Fact]
    public async Task Entry_ids_survive_a_save_and_a_reopen()
    {
        using var context = new VaultTestContext();
        EntryId documents;
        EntryId nested;
        EntryId file;

        await using (IVaultSession session = await context.CreateAsync())
        {
            documents = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
            nested = await session.CreateFolderAsync(documents, "2026", CancellationToken.None);
            string source = context.WriteSourceFile("notes.txt", "hello vault"u8.ToArray());
            ImportResult result = await session.ImportAsync(nested, [source], new ImportOptions(), null, CancellationToken.None);
            file = result.Imported.Single();

            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            Assert.Equal("Documents", session.Find(documents)!.Name);
            Assert.Equal("notes.txt", session.Find(file)!.Name);
        }

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal("Documents", reopened.Find(documents)!.Name);
        Assert.Equal("2026", reopened.Find(nested)!.Name);
        Assert.Equal("notes.txt", reopened.Find(file)!.Name);
        Assert.Equal("\\Documents\\2026\\notes.txt", reopened.FormatPath(file));

        EntryId again = await reopened.CreateFolderAsync(EntryId.Root, "Fresh", CancellationToken.None);
        Assert.True(again.Value > file.Value);
    }

    [Fact]
    public async Task Copying_a_file_makes_a_second_blob_with_the_same_content()
    {
        using var context = new VaultTestContext();
        byte[] content = VaultTestContext.Bytes(40_000, 5);
        EntryId original;
        EntryId copy;

        await using (IVaultSession session = await context.CreateAsync())
        {
            string source = context.WriteSourceFile("payload.bin", content);
            ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);
            original = imported.Imported.Single();
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

            EntryId folder = await session.CreateFolderAsync(EntryId.Root, "Copies", CancellationToken.None);
            IReadOnlyList<EntryId> copies = await session.CopyAsync([original], folder, CancellationToken.None);
            copy = copies.Single();

            Assert.Equal(content, await VaultTestContext.ReadAllAsync(session, copy));
            await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);
        }

        await using IVaultSession reopened = await context.OpenAsync();
        Assert.Equal(content, await VaultTestContext.ReadAllAsync(reopened, original));
        Assert.Equal(content, await VaultTestContext.ReadAllAsync(reopened, copy));

        var inner = (global::Bastion.Core.Session.VaultSession)reopened;
        byte[] originalBlobId = inner.Tree.Find(original.Value)!.Content!.BlobId;
        byte[] copyBlobId = inner.Tree.Find(copy.Value)!.Content!.BlobId;
        Assert.NotEqual(originalBlobId, copyBlobId);

        VerifyReport report = await reopened.VerifyAsync(null, CancellationToken.None);
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task Copying_into_the_same_folder_gives_the_copy_a_unique_name()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        string source = context.WriteSourceFile("archive.tar.gz", VaultTestContext.Bytes(128, 6));
        ImportResult imported = await session.ImportAsync(EntryId.Root, [source], new ImportOptions(), null, CancellationToken.None);

        IReadOnlyList<EntryId> copies = await session.CopyAsync([imported.Imported.Single()], EntryId.Root, CancellationToken.None);
        Assert.Equal("archive.tar (2).gz", session.Find(copies.Single())!.Name);
    }

    [Fact]
    public async Task Search_finds_entries_by_substring_within_a_scope()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId documents = await session.CreateFolderAsync(EntryId.Root, "Documents", CancellationToken.None);
        await session.CreateFolderAsync(documents, "Invoices", CancellationToken.None);
        await session.CreateFolderAsync(EntryId.Root, "Invoice archive", CancellationToken.None);

        Assert.Equal(2, session.Search("invoice", null, 10, CancellationToken.None).Count);
        Assert.Single(session.Search("invoice", documents, 10, CancellationToken.None));
        Assert.Single(session.Search("invoice", null, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Discarding_changes_returns_the_tree_to_the_last_save()
    {
        using var context = new VaultTestContext();
        await using IVaultSession session = await context.CreateAsync();

        EntryId kept = await session.CreateFolderAsync(EntryId.Root, "Kept", CancellationToken.None);
        await session.SaveAsync(SaveOptions.Default, null, CancellationToken.None);

        await session.CreateFolderAsync(EntryId.Root, "Temporary", CancellationToken.None);
        await session.RenameAsync(kept, "Renamed", CancellationToken.None);
        Assert.True(session.IsDirty);

        await session.DiscardChangesAsync(CancellationToken.None);

        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.Single(session.GetChildren(EntryId.Root));
        Assert.Equal("Kept", session.Find(kept)!.Name);
    }
}
