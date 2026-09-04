using System.Security.Cryptography;
using BastionVault.Core.Crypto;
using BastionVault.Core.Format;
using Microsoft.Win32.SafeHandles;
using IoPath = System.IO.Path;
using SysFile = System.IO.File;

namespace BastionVault.Core.Session;

/// <summary>Saving, credential changes, discarding, locking and disposal.</summary>
internal sealed partial class VaultSession
{
    /// <inheritdoc />
    public async Task SaveAsync(SaveOptions options, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();

        VaultCrypto source = RequireCrypto();
        PendingCredentials? pending;
        List<TreeNode> entries;
        uint nextEntryId;
        FileStat expected;
        VaultHeader header;
        ulong counter;

        lock (_treeGate)
        {
            pending = _pendingCredentials;
            entries = [.. Tree.CanonicalOrder()];
            nextEntryId = Tree.NextEntryId;
            expected = _stat;
            header = _header;
            counter = _saveCounter;
        }

        SaveMode mode = pending?.Mode == CredentialChangeMode.Rekey ? SaveMode.Rekey : SaveMode.Compact;

        // Lock() runs on any thread without taking the operation gate and zeroes the session's key set.
        // A save that has already replaced the file must still be able to finish and verify what it
        // wrote, so it works on copies it owns rather than on the session's live key material.
        VaultCrypto? saveSource = null;
        VaultCrypto? rekeyed = null;
        KeyMaterial? wrapKek = null;
        SaveResult result;
        try
        {
            saveSource = CopyOf(source);
            if (mode == SaveMode.Rekey)
            {
                rekeyed = CopyOf(pending!.NewVaultKey!);
            }

            wrapKek = pending is null ? null : CopyKeyOf(pending.Kek);

            var request = new SaveRequest
            {
                DestinationPath = Path,
                ReplaceExisting = true,
                VaultFile = FileHandle,
                ExpectedStat = expected,
                Entries = entries,
                NextEntryId = nextEntryId,
                SourceCrypto = saveSource,
                DestinationCrypto = rekeyed ?? saveSource,
                Mode = mode,
                Wrap = BuildWrapPlan(header, pending, wrapKek),
                SaveCounter = counter + 1,
                SavedUtc = Clock.UtcNow,
                SizeObfuscation = options.SizeObfuscation,
                Operation = VaultOperation.Save,
            };

            result = await new SaveWriter(Random, _paths).RunAsync(request, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rekeyed?.Dispose();
            ReopenAfterFailure();
            Exception translated = IoGuard.Translate(ex, Path);
            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }
        finally
        {
            saveSource?.Dispose();
            wrapKek?.Dispose();
        }

        // Step 9 runs even when the file cannot be reopened: the save is committed and verified, so the
        // session's view has to match what is on disk or the next save reports ChangedOnDisk against the
        // very file it just wrote.
        Exception? reopenFailure = AdoptSavedState(result, rekeyed);
        TrySweepOrphans();
        ClearDirty();
        Raise(VaultChangeKind.Saved, [], EntryId.Root);

        if (reopenFailure is not null)
        {
            throw IoGuard.Translate(reopenFailure, Path);
        }
    }

    /// <summary>
    /// Copies a key set so an in-flight operation survives a concurrent <see cref="Lock"/>.
    /// </summary>
    /// <param name="crypto">The session key set to copy.</param>
    private static VaultCrypto CopyOf(VaultCrypto crypto) => VaultCrypto.Adopt(CopyKeyOf(crypto.VaultKey));

    /// <summary>Copies a vault key so an in-flight operation survives a concurrent <see cref="Lock"/>.</summary>
    /// <param name="key">The key to copy.</param>
    private static VaultCrypto CopyOf(KeyMaterial key) => VaultCrypto.Adopt(CopyKeyOf(key));

    /// <summary>Copies key material, reporting a lock that beat the copy as <c>SessionLocked</c>.</summary>
    /// <param name="key">The key to copy.</param>
    private static KeyMaterial CopyKeyOf(KeyMaterial key)
    {
        try
        {
            return KeyMaterial.From(key.Span);
        }
        catch (ObjectDisposedException ex)
        {
            throw IoGuard.Translate(ex, null);
        }
    }

    /// <inheritdoc />
    public async Task SaveCopyAsync(
        string newPath,
        Passphrase password,
        KeyFile? keyFile,
        KdfParameters kdf,
        SaveOptions options,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(kdf);
        ArgumentNullException.ThrowIfNull(options);
        kdf.Validate();

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireUnlocked();

        string destination = IoPath.GetFullPath(newPath);
        if (SysFile.Exists(destination) || Directory.Exists(destination))
        {
            throw new VaultIoException(VaultErrorCode.IoError, $"A file already exists at {destination}.")
            { OffendingPath = destination };
        }

        Credentials.PreflightMemory(kdf);

        VaultCrypto source = RequireCrypto();
        List<TreeNode> entries;
        uint nextEntryId;
        ulong counter;
        lock (_treeGate)
        {
            entries = [.. Tree.CanonicalOrder()];
            nextEntryId = Tree.NextEntryId;
            counter = _saveCounter;
        }

        byte[] salt = new byte[32];
        Random.Fill(salt);

        var kdfProgress = new ProgressThrottle(progress, VaultOperation.KeyDerivation);
        using KeyMaterial kek = await Credentials
            .DeriveKekAsync(_kdf, password, keyFile, salt, kdf, kdfProgress, ct)
            .ConfigureAwait(false);

        // "Save a copy" always re-keys so the two files never share a key space (FORMAT.md section 8.4).
        using VaultCrypto fresh = VaultCrypto.Create(Random);
        using VaultCrypto saveSource = CopyOf(source);

        var request = new SaveRequest
        {
            DestinationPath = destination,
            ReplaceExisting = false,
            Entries = entries,
            NextEntryId = nextEntryId,
            SourceCrypto = saveSource,
            DestinationCrypto = fresh,
            Mode = SaveMode.Rekey,
            Wrap = new WrapPlan { Kdf = kdf, KdfSalt = salt, Kek = kek },
            SaveCounter = counter,
            SavedUtc = Clock.UtcNow,
            SizeObfuscation = options.SizeObfuscation,
            Operation = VaultOperation.SaveCopy,
        };

        await new SaveWriter(Random, _paths).RunAsync(request, progress, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ChangeCredentialsAsync(
        Passphrase newPassword,
        KeyFile? newKeyFile,
        KdfParameters kdf,
        CredentialChangeMode mode,
        IProgress<VaultProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        ArgumentNullException.ThrowIfNull(kdf);
        kdf.Validate();

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        RequireWritable();
        RequireUnlocked();
        Credentials.PreflightMemory(kdf);

        byte[] salt = new byte[32];
        Random.Fill(salt);

        var throttle = new ProgressThrottle(progress, VaultOperation.ChangeCredentials);
        KeyMaterial kek = await Credentials.DeriveKekAsync(_kdf, newPassword, newKeyFile, salt, kdf, throttle, ct)
            .ConfigureAwait(false);

        PendingCredentials credentials;
        try
        {
            credentials = new PendingCredentials
            {
                Kdf = kdf,
                KdfSalt = salt,
                Kek = kek,
                Mode = mode,
                NewVaultKey = mode == CredentialChangeMode.Rekey ? KeyMaterial.Random(32, Random) : null,
            };
        }
        catch
        {
            kek.Dispose();
            throw;
        }

        lock (_treeGate)
        {
            PendingCredentials? previous = _pendingCredentials;
            _credentialBin.Add(credentials);
            _pendingCredentials = credentials;
            _undo.Push(new CredentialStep(previous, credentials));
        }

        throttle.Complete(0, 1);
        MarkDirty();
        Raise(VaultChangeKind.EntryUpdated, [], EntryId.Root);
    }

    /// <inheritdoc />
    public Task DiscardChangesAsync(CancellationToken ct)
    {
        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();

        lock (_treeGate)
        {
            Tree = BuildTree(_storedIndex, FileHandle, _header);
            _deletedStored.Clear();
            _undo.Clear();
            _pendingCredentials = null;
            DisposeCredentialBin();
            Staging.Clear();
        }

        ClearDirty();
        Raise(VaultChangeKind.Reloaded, [], EntryId.Root);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Lock()
    {
        bool flipped = false;
        try
        {
            lock (_treeGate)
            {
                if (!_locked)
                {
                    _crypto?.Dispose();
                    _crypto = null;
                    _pendingCredentials = null;
                    DisposeCredentialBin();
                    _undo.DropCredentialSteps();
                    _locked = true;
                    flipped = true;
                }
            }

            if (flipped)
            {
                Raise(VaultChangeKind.LockChanged, [], EntryId.Root);
                TrySweepOrphans();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Lock is the crash-handler entry point: it must never throw, and the keys are already gone.
        }
    }

    /// <inheritdoc />
    public async Task UnlockAsync(Passphrase password, KeyFile? keyFile, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();
        if (!IsLocked)
        {
            return;
        }

        VaultHeader header;
        lock (_treeGate)
        {
            header = _header;
        }

        Credentials.PreflightMemory(header.Kdf);

        var throttle = new ProgressThrottle(progress, VaultOperation.KeyDerivation);
        using KeyMaterial kek = await Credentials
            .DeriveKekAsync(_kdf, password, keyFile, header.KdfSalt, header.Kdf, throttle, ct)
            .ConfigureAwait(false);

        KeyMaterial vaultKey = HeaderCipher.UnwrapVaultKey(kek.Span, header.WrapNonce, header.WrappedVaultKey, header.BuildWrapAad());
        VaultCrypto crypto = VaultCrypto.Adopt(vaultKey);

        // FORMAT.md section 8.8: the key that comes back must be the key this session was working with.
        if (!CryptographicOperations.FixedTimeEquals(crypto.VaultId, _vaultId))
        {
            crypto.Dispose();
            throw new VaultAuthenticationException(
                VaultErrorCode.AuthenticationFailed,
                "wrong password or keyfile, or the vault header has been altered");
        }

        lock (_treeGate)
        {
            _crypto = crypto;
            _locked = false;
        }

        throttle.Complete(0, 1);
        Raise(VaultChangeKind.LockChanged, [], EntryId.Root);
    }

    /// <inheritdoc />
    public async Task<bool> VerifyPasswordAsync(Passphrase password, KeyFile? keyFile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);

        using GateScope scope = EnterGate();
        ct.ThrowIfCancellationRequested();

        VaultHeader header;
        byte[] expectedVaultId;
        lock (_treeGate)
        {
            header = _header;
            expectedVaultId = _vaultId;
        }

        Credentials.PreflightMemory(header.Kdf);

        using KeyMaterial kek = await Credentials
            .DeriveKekAsync(_kdf, password, keyFile, header.KdfSalt, header.Kdf, null, ct)
            .ConfigureAwait(false);

        KeyMaterial vaultKey;
        try
        {
            vaultKey = HeaderCipher.UnwrapVaultKey(kek.Span, header.WrapNonce, header.WrappedVaultKey, header.BuildWrapAad());
        }
        catch (VaultAuthenticationException)
        {
            // The whole point of the call is to answer the question rather than to raise it.
            return false;
        }

        // Nothing is adopted: the derived key set is compared and dropped again, so the session is
        // exactly as it was, whether the answer is yes or no.
        using VaultCrypto candidate = VaultCrypto.Adopt(vaultKey);
        return CryptographicOperations.FixedTimeEquals(candidate.VaultId, expectedVaultId);
    }

    /// <inheritdoc />
    public void ZeroKeys() => Lock();

    /// <summary>Locks the session, drops staging and releases the vault file handle.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Lock();
        Staging.Dispose();
        FileHandle.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Decides how the new header protects the vault key.</summary>
    /// <param name="header">The header currently on disk.</param>
    /// <param name="pending">A pending credential change, if any.</param>
    /// <param name="kek">The save's own copy of the pending KEK, when there is one.</param>
    private static WrapPlan BuildWrapPlan(VaultHeader header, PendingCredentials? pending, KeyMaterial? kek) => pending is null
        ? new WrapPlan
        {
            Kdf = header.Kdf,
            KdfSalt = header.KdfSalt,
            WrapNonce = header.WrapNonce,
            WrappedVaultKey = header.WrappedVaultKey,
        }
        : new WrapPlan
        {
            Kdf = pending.Kdf,
            KdfSalt = pending.KdfSalt,
            Kek = kek,
        };

    /// <summary>Adopts everything a successful save produced (FORMAT.md section 8.3 step 9).</summary>
    /// <param name="result">What the writer wrote.</param>
    /// <param name="rekeyed">The new key set when the save re-keyed the vault.</param>
    /// <returns>The failure that stopped the file from being reopened, or <see langword="null"/>.</returns>
    private Exception? AdoptSavedState(SaveResult result, VaultCrypto? rekeyed)
    {
        Exception? reopenFailure = null;
        try
        {
            SafeFileHandle handle = OpenVaultHandle(Path);
            FileHandle.Adopt(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The vault is written and verified; only this session's read handle is missing. Everything
            // else still has to be adopted, and the caller reports the handle separately.
            reopenFailure = ex;
        }

        lock (_treeGate)
        {
            _header = result.Header;
            _storedIndex = result.Index;
            _saveCounter = result.Index.SaveCounter;
            _lastSavedUtc = TreeModel.ToUtc(result.Index.SavedUtcTicks);
            _stat = result.Stat;
            _layout = CaptureLayout(result.Index, result.Header, result.FileLength);
            OpenedFromIndexCopy = false;

            foreach (TreeNode node in Tree.CanonicalOrder())
            {
                if (node.Kind == EntryKind.File && result.Placements.TryGetValue(node.Id, out BlobPlacement placement))
                {
                    node.Content = new BlobRef
                    {
                        BlobId = placement.BlobId,
                        Source = new StoredBlobSource(FileHandle, placement.FileOffset, placement.BlobLength),
                        Length = node.Content?.Length ?? 0,
                        ChunkSize = placement.ChunkSize,
                        BlobHash = placement.BlobHash,
                    };
                }

                node.State = EntryState.Stored;
            }

            _deletedStored.Clear();
            _undo.Clear();

            if (rekeyed is not null)
            {
                // The vault id follows the key the file is now wrapped with, whatever the lock state:
                // Unlock's section 8.8 check compares against it.
                _vaultId = (byte[])rekeyed.VaultId.Clone();
                if (_locked)
                {
                    // Lock landed during the save. The session stays locked; the new key set is dropped
                    // rather than silently reinstated behind the lock.
                    rekeyed.Dispose();
                }
                else
                {
                    _crypto?.Dispose();
                    _crypto = rekeyed;
                }
            }

            _pendingCredentials = null;
            DisposeCredentialBin();
            Staging.Clear();
        }

        return reopenFailure;
    }

    /// <summary>Reopens the vault after a failed save so the session keeps working on the original file.</summary>
    private void ReopenAfterFailure()
    {
        if (FileHandle.IsOpen || !SysFile.Exists(Path))
        {
            return;
        }

        try
        {
            FileHandle.Adopt(OpenVaultHandle(Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original error is the one worth reporting; the session simply stays without a handle.
        }
    }

    /// <summary>Disposes every pending credential this session ever derived.</summary>
    private void DisposeCredentialBin()
    {
        foreach (PendingCredentials credentials in _credentialBin)
        {
            credentials.Dispose();
        }

        _credentialBin.Clear();
    }

    /// <summary>Removes orphaned staging containers and temporary files next to the vault (FORMAT.md section 8.5).</summary>
    private void TrySweepOrphans()
    {
        try
        {
            string? directory = IoPath.GetDirectoryName(Path);
            if (directory is not null)
            {
                OrphanSweeper.Sweep([directory], CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sweeping is opportunistic housekeeping and never fails an operation.
        }
    }

    /// <summary>Opens the vault file the way a session holds it (FORMAT.md section 8.2).</summary>
    /// <param name="path">Path of the vault file.</param>
    internal static SafeFileHandle OpenVaultHandle(string path) =>
        SysFile.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
}
