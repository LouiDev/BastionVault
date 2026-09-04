using System.Buffers;
using System.Security.Cryptography;
using BastionVault.Core.Crypto;
using BastionVault.Core.Format;
using Microsoft.Win32.SafeHandles;
using IoPath = System.IO.Path;

namespace BastionVault.Core.Session;

/// <summary>How the header of the file being written protects the vault key.</summary>
internal sealed class WrapPlan
{
    /// <summary>Argon2id parameters to store.</summary>
    public required KdfParameters Kdf { get; init; }

    /// <summary>The 32-byte KDF salt to store.</summary>
    public required byte[] KdfSalt { get; init; }

    /// <summary>The wrap nonce to reuse, or <see langword="null"/> to generate a fresh one.</summary>
    public byte[]? WrapNonce { get; init; }

    /// <summary>The 48-byte wrapped vault key to copy verbatim, or <see langword="null"/> to wrap anew.</summary>
    public byte[]? WrappedVaultKey { get; init; }

    /// <summary>The key-encryption key to wrap with; required when <see cref="WrappedVaultKey"/> is null.</summary>
    public KeyMaterial? Kek { get; init; }
}

/// <summary>Everything one run of the save state machine needs.</summary>
internal sealed class SaveRequest
{
    /// <summary>Absolute path the file is written to.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>True when an existing vault is being replaced (FORMAT.md section 8.3 steps 1, 5 and 6).</summary>
    public required bool ReplaceExisting { get; init; }

    /// <summary>The session file handle, closed before the replace step.</summary>
    public VaultFileHandle? VaultFile { get; init; }

    /// <summary>Length and last-write time captured at open or at the last save.</summary>
    public FileStat? ExpectedStat { get; init; }

    /// <summary>Every entry, in the canonical order of FORMAT.md section 4.5.</summary>
    public required IReadOnlyList<TreeNode> Entries { get; init; }

    /// <summary>Next entry id to store in the index.</summary>
    public required uint NextEntryId { get; init; }

    /// <summary>Keys the existing and staged blobs are encrypted under.</summary>
    public required VaultCrypto SourceCrypto { get; init; }

    /// <summary>Keys the new file is written with.</summary>
    public required VaultCrypto DestinationCrypto { get; init; }

    /// <summary>Compact (verbatim blob copies) or Rekey (everything re-encrypted).</summary>
    public required SaveMode Mode { get; init; }

    /// <summary>How the header protects the vault key.</summary>
    public required WrapPlan Wrap { get; init; }

    /// <summary>Save counter to store.</summary>
    public required ulong SaveCounter { get; init; }

    /// <summary>Timestamp to store.</summary>
    public required DateTimeOffset SavedUtc { get; init; }

    /// <summary>Pad the data section up to the obfuscation ladder.</summary>
    public required bool SizeObfuscation { get; init; }

    /// <summary>Operation reported through progress.</summary>
    public required VaultOperation Operation { get; init; }
}

/// <summary>Where one blob ended up in the file that was just written.</summary>
/// <param name="BlobId">The blob id it was written under.</param>
/// <param name="FileOffset">Absolute offset in the new file.</param>
/// <param name="BlobLength">Ciphertext length.</param>
/// <param name="BlobHash">SHA-256 over the ciphertext.</param>
/// <param name="ChunkSize">Chunk size used.</param>
internal readonly record struct BlobPlacement(byte[] BlobId, long FileOffset, long BlobLength, byte[] BlobHash, uint ChunkSize);

/// <summary>The outcome of a successful save.</summary>
/// <param name="Header">The header that was written.</param>
/// <param name="Index">The index that was written.</param>
/// <param name="Placements">Where every file entry landed, keyed by entry id.</param>
/// <param name="FileLength">Length of the new file.</param>
/// <param name="Stat">Length and last-write time of the new file.</param>
internal sealed record SaveResult(
    VaultHeader Header,
    VaultIndex Index,
    IReadOnlyDictionary<uint, BlobPlacement> Placements,
    long FileLength,
    FileStat Stat);

/// <summary>
/// The save state machine of FORMAT.md section 8.3 with the two modes of section 8.4. It writes a
/// temporary file next to the destination, swaps it in atomically and verifies the result.
/// </summary>
internal sealed class SaveWriter
{
    private const int ReplaceAttempts = 6;
    private const int CopyBufferSize = 1 << 20;
    private const int VerifiedBlobCount = 3;

    private readonly IRandomSource _random;
    private readonly IVaultPaths _paths;

    /// <summary>Creates a writer.</summary>
    /// <param name="random">Randomness seam (nonces, blob ids, padding, retry jitter).</param>
    /// <param name="paths">Temp and backup naming seam.</param>
    public SaveWriter(IRandomSource random, IVaultPaths paths)
    {
        _random = random;
        _paths = paths;
    }

    /// <summary>Runs the whole state machine.</summary>
    /// <param name="request">What to write and where.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation token; ignored from the replace step onwards.</param>
    public async Task<SaveResult> RunAsync(SaveRequest request, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<BlobJob> jobs = PlanBlobs(request);
        long contentLength = jobs.Count == 0 ? 0 : jobs[^1].Offset + jobs[^1].BlobLength;
        long padding = request.SizeObfuscation ? PadLadder.Obfuscation(contentLength) - contentLength : 0;
        long dataSectionLength = contentLength + padding;

        VaultIndex index = BuildIndex(request, jobs, dataSectionLength, padding);
        byte[] indexPlaintext = IndexSerializer.Serialize(index);
        long indexLength = indexPlaintext.Length + VaultLimits.TagSize;
        long fileLength = VaultHeader.Size + (2 * indexLength) + dataSectionLength;

        // Step 0: preconditions.
        RequireWritableDestination(request);
        RequireFreeSpace(request.DestinationPath, fileLength + StagingStore.SaveHeadroomBytes);

        // Step 1: the vault must not have changed underneath the session.
        RequireUnchanged(request);

        VaultHeader header = BuildHeader(request, indexLength);
        var throttle = new ProgressThrottle(progress, request.Operation, contentLength, jobs.Count);
        throttle.Start(IoPath.GetFileName(request.DestinationPath));

        string tempPath = _paths.TempFileFor(request.DestinationPath);
        bool tempConsumed = false;
        try
        {
            // Steps 2 to 4: write and flush the complete new file.
            IReadOnlyDictionary<uint, BlobPlacement> placements =
                await WriteTempAsync(request, header, index, jobs, dataSectionLength, padding, indexLength, tempPath, throttle, ct)
                    .ConfigureAwait(false);

            // Steps 5 to 7: swap the file in. The two-move fallback takes the temporary file out of
            // the cleanup path itself, because after its first move the temp is the only copy.
            string? backupPath = await SwapInAsync(
                request, tempPath, throttle, contentLength, jobs.Count, () => tempConsumed = true).ConfigureAwait(false);
            tempConsumed = true;

            // Step 8: post-save verification.
            FileStat stat = Verify(request, header, index, backupPath);

            // Step 9: the backup is no longer needed.
            if (backupPath is not null)
            {
                TryDelete(backupPath);
            }

            throttle.Complete(contentLength, jobs.Count, IoPath.GetFileName(request.DestinationPath));
            return new SaveResult(header, index, placements, fileLength, stat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw IoGuard.Translate(ex, request.DestinationPath);
        }
        finally
        {
            if (!tempConsumed)
            {
                TryDelete(tempPath);
            }
        }
    }

    /// <summary>Decides for every file entry whether its blob is copied verbatim or re-encrypted.</summary>
    /// <param name="request">The save request.</param>
    private List<BlobJob> PlanBlobs(SaveRequest request)
    {
        bool sameKeySpace = request.SourceCrypto.SharesKeySpaceWith(request.DestinationCrypto);
        var jobs = new List<BlobJob>();
        long offset = 0;

        foreach (TreeNode node in request.Entries)
        {
            if (node.Kind != EntryKind.File)
            {
                continue;
            }

            BlobRef content = node.Content
                ?? throw new InvalidOperationException($"File entry {node.Id} has no content reference.");

            bool verbatim = request.Mode == SaveMode.Compact && !content.RequiresReencrypt;

            // FORMAT.md section 8.3 step 3: a verbatim copy is only correct while both sides share a key space.
            if (verbatim && !sameKeySpace)
            {
                throw new InvalidOperationException(
                    "A blob cannot be copied verbatim into a file that uses a different vault key; the save must re-encrypt it.");
            }

            byte[] destinationBlobId = verbatim ? content.BlobId : NewBlobId();
            long blobLength = ChunkCipher.BlobLength(content.Length, content.ChunkSize);

            jobs.Add(new BlobJob
            {
                Node = node,
                Content = content,
                Verbatim = verbatim,
                DestinationBlobId = destinationBlobId,
                Offset = offset,
                BlobLength = blobLength,
            });

            offset += blobLength;
        }

        return jobs;
    }

    /// <summary>Builds the index that will be written; blob hashes are filled in while the data is written.</summary>
    /// <param name="request">The save request.</param>
    /// <param name="jobs">The planned blobs.</param>
    /// <param name="dataSectionLength">Length of the data section.</param>
    /// <param name="padding">Trailing random bytes inside the data section.</param>
    private static VaultIndex BuildIndex(SaveRequest request, List<BlobJob> jobs, long dataSectionLength, long padding)
    {
        var index = new VaultIndex
        {
            SaveCounter = request.SaveCounter,
            SavedUtcTicks = TreeModel.ToTicks(request.SavedUtc),
            DataSectionLength = dataSectionLength,
            DataPaddingLength = padding,
            NextEntryId = request.NextEntryId,
            Entries = [],
        };

        var byNode = new Dictionary<uint, BlobJob>();
        foreach (BlobJob job in jobs)
        {
            byNode[job.Node.Id] = job;
        }

        foreach (TreeNode node in request.Entries)
        {
            var entry = new IndexEntry
            {
                Kind = node.Kind,
                Id = node.Id,
                ParentId = node.ParentId,
                Name = node.Name,
                CreatedUtcTicks = Clamp(node.CreatedUtcTicks),
                ModifiedUtcTicks = Clamp(node.ModifiedUtcTicks),
                Attributes = 0,
                Comment = node.Comment,
            };

            if (node.Kind == EntryKind.File)
            {
                BlobJob job = byNode[node.Id];
                entry.BlobId = job.DestinationBlobId;
                entry.DataOffset = job.Offset;
                entry.Length = job.Content.Length;
                entry.ChunkSize = job.Content.ChunkSize;
                entry.BlobHash = job.Verbatim ? job.Content.BlobHash : new byte[32];
                job.Entry = entry;
            }

            index.Entries.Add(entry);
        }

        return index;
    }

    /// <summary>Builds the header of the new file, wrapping the vault key when the credentials changed.</summary>
    /// <param name="request">The save request.</param>
    /// <param name="indexLength">Encrypted index length.</param>
    private VaultHeader BuildHeader(SaveRequest request, long indexLength)
    {
        byte[] indexNonce = new byte[12];
        byte[] indexCopyNonce = new byte[12];
        _random.Fill(indexNonce);
        do
        {
            _random.Fill(indexCopyNonce);
        }
        while (indexNonce.AsSpan().SequenceEqual(indexCopyNonce));

        byte[] wrapNonce = request.Wrap.WrapNonce ?? NewNonce();

        // The salt is copied: a pending credential change owns its salt buffer and zeroes it when the
        // save adopts the new state, and the header that survives the save must not alias that buffer.
        var draft = new VaultHeader
        {
            FormatVersion = 1,
            Flags = 0,
            Kdf = request.Wrap.Kdf,
            KdfSalt = (byte[])request.Wrap.KdfSalt.Clone(),
            WrapNonce = wrapNonce,
            WrappedVaultKey = request.Wrap.WrappedVaultKey ?? new byte[48],
            IndexNonce = indexNonce,
            IndexCopyNonce = indexCopyNonce,
            IndexLength = indexLength,
        };

        if (request.Wrap.WrappedVaultKey is not null)
        {
            return draft;
        }

        KeyMaterial kek = request.Wrap.Kek
            ?? throw new InvalidOperationException("A save that rewraps the vault key needs a key-encryption key.");

        byte[] wrapped = new byte[48];
        HeaderCipher.WrapVaultKey(kek.Span, wrapNonce, request.DestinationCrypto.VaultKey.Span, draft.BuildWrapAad(), wrapped);

        return new VaultHeader
        {
            FormatVersion = draft.FormatVersion,
            Flags = draft.Flags,
            Kdf = draft.Kdf,
            KdfSalt = draft.KdfSalt,
            WrapNonce = draft.WrapNonce,
            WrappedVaultKey = wrapped,
            IndexNonce = draft.IndexNonce,
            IndexCopyNonce = draft.IndexCopyNonce,
            IndexLength = draft.IndexLength,
        };
    }

    /// <summary>
    /// Writes the complete new file (FORMAT.md section 8.3 steps 2 to 4). The payload is written in one
    /// forward pass; the header and the primary index are filled in afterwards because the blob hashes
    /// of re-encrypted blobs only exist once their ciphertext has been produced.
    /// </summary>
    /// <param name="request">The save request.</param>
    /// <param name="header">Header of the new file.</param>
    /// <param name="index">Index of the new file; blob hashes are completed here.</param>
    /// <param name="jobs">The planned blobs.</param>
    /// <param name="dataSectionLength">Length of the data section.</param>
    /// <param name="padding">Trailing random bytes inside the data section.</param>
    /// <param name="indexLength">Encrypted index length.</param>
    /// <param name="tempPath">Path of the temporary file.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IReadOnlyDictionary<uint, BlobPlacement>> WriteTempAsync(
        SaveRequest request,
        VaultHeader header,
        VaultIndex index,
        List<BlobJob> jobs,
        long dataSectionLength,
        long padding,
        long indexLength,
        string tempPath,
        ProgressThrottle throttle,
        CancellationToken ct)
    {
        var placements = new Dictionary<uint, BlobPlacement>();
        long dataSectionOffset = VaultHeader.Size + indexLength;

        FileStream stream = OpenTemp(tempPath, dataSectionOffset + dataSectionLength + indexLength);
        await using (stream.ConfigureAwait(false))
        {
            stream.Position = dataSectionOffset;

            var counters = new WriteCounters();
            for (int i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                BlobJob job = jobs[i];
                counters.Items = i;
                byte[] hash = job.Verbatim
                    ? await CopyVerbatimAsync(stream, job, throttle, counters, ct).ConfigureAwait(false)
                    : await ReencryptAsync(stream, request, job, throttle, counters, ct).ConfigureAwait(false);

                job.Entry!.BlobHash = hash;
                placements[job.Node.Id] = new BlobPlacement(
                    job.DestinationBlobId,
                    dataSectionOffset + job.Offset,
                    job.BlobLength,
                    hash,
                    job.Content.ChunkSize);
            }

            await WritePaddingAsync(stream, padding, ct).ConfigureAwait(false);

            byte[] indexPlaintext = IndexSerializer.Serialize(index);
            if (indexPlaintext.Length + VaultLimits.TagSize != indexLength)
            {
                throw new InvalidOperationException("The index changed length while the data section was written.");
            }

            byte[] indexAad = header.BuildIndexAad();
            byte[] primary = HeaderCipher.EncryptIndex(request.DestinationCrypto.IndexKey.Span, header.IndexNonce, indexPlaintext, indexAad);
            byte[] copy = HeaderCipher.EncryptIndex(request.DestinationCrypto.IndexKey.Span, header.IndexCopyNonce, indexPlaintext, indexAad);
            CryptographicOperations.ZeroMemory(indexPlaintext);

            await stream.WriteAsync(copy, ct).ConfigureAwait(false);

            stream.Position = VaultHeader.Size;
            await stream.WriteAsync(primary, ct).ConfigureAwait(false);

            byte[] headerBytes = new byte[VaultHeader.Size];
            header.Write(headerBytes);
            stream.Position = 0;
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);

            stream.Flush(flushToDisk: true);
        }

        return placements;
    }

    /// <summary>Copies a blob byte for byte; only legal while source and destination share a vault key.</summary>
    /// <param name="stream">Destination stream, positioned at the blob offset.</param>
    /// <param name="job">The blob to copy.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="counters">Running byte and item counters.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<byte[]> CopyVerbatimAsync(
        FileStream stream, BlobJob job, ProgressThrottle throttle, WriteCounters counters, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long remaining = job.BlobLength;
            long offset = 0;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int take = (int)Math.Min(remaining, buffer.Length);
                job.Content.Source.Read(offset, buffer.AsSpan(0, take));
                await stream.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                offset += take;
                remaining -= take;
                counters.Bytes += take;
                throttle.Report(counters.Bytes, counters.Items, job.Node.Name);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return job.Content.BlobHash;
    }

    /// <summary>Streams a blob decrypt-then-encrypt into the new file under a fresh blob id.</summary>
    /// <param name="stream">Destination stream, positioned at the blob offset.</param>
    /// <param name="request">The save request.</param>
    /// <param name="job">The blob to re-encrypt.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="counters">Running byte and item counters.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task<byte[]> ReencryptAsync(
        FileStream stream, SaveRequest request, BlobJob job, ProgressThrottle throttle, WriteCounters counters, CancellationToken ct)
    {
        uint chunkSize = job.Content.ChunkSize;
        byte[] cipherIn = ArrayPool<byte>.Shared.Rent((int)chunkSize + ChunkCipher.TagSize);
        byte[] plain = ArrayPool<byte>.Shared.Rent((int)chunkSize);
        byte[] cipherOut = ArrayPool<byte>.Shared.Rent((int)chunkSize + ChunkCipher.TagSize);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var reader = new BlobReader(
            job.Content.Source, request.SourceCrypto, job.Content.BlobId, job.Content.Length, chunkSize,
            TreeModel.FormatPath(job.Node));
        using KeyMaterial destinationKey =
            VaultKeys.DeriveBlobKey(request.DestinationCrypto.VaultKey.Span, job.DestinationBlobId);
        using var aes = new AesGcm(destinationKey.Span, ChunkCipher.TagSize);

        try
        {
            for (uint chunk = 0; chunk < reader.ChunkCount; chunk++)
            {
                ct.ThrowIfCancellationRequested();
                int plainLength = reader.ReadPlaintextChunk(chunk, cipherIn, plain);
                bool isLast = chunk == reader.ChunkCount - 1;
                int outLength = plainLength + ChunkCipher.TagSize;

                ChunkCipher.EncryptChunk(
                    aes, request.DestinationCrypto.VaultId, job.DestinationBlobId, chunk, isLast,
                    plain.AsSpan(0, plainLength), cipherOut.AsSpan(0, outLength));

                hash.AppendData(cipherOut.AsSpan(0, outLength));
                await stream.WriteAsync(cipherOut.AsMemory(0, outLength), ct).ConfigureAwait(false);
                counters.Bytes += outLength;
                throttle.Report(counters.Bytes, counters.Items, job.Node.Name);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipherIn);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
            ArrayPool<byte>.Shared.Return(cipherOut);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Writes the trailing random bytes of the data section.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="padding">Number of bytes to write.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task WritePaddingAsync(FileStream stream, long padding, CancellationToken ct)
    {
        if (padding <= 0)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long remaining = padding;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int take = (int)Math.Min(remaining, buffer.Length);
                _random.Fill(buffer.AsSpan(0, take));
                await stream.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                remaining -= take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Closes the vault handle, re-checks the on-disk state and swaps the temporary file in
    /// (FORMAT.md section 8.3 steps 5 to 7). From here on cancellation is ignored.
    /// </summary>
    /// <param name="request">The save request.</param>
    /// <param name="tempPath">The finished temporary file.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="bytesDone">Bytes written so far, for the progress report.</param>
    /// <param name="itemsDone">Blobs written so far, for the progress report.</param>
    /// <param name="onTempConsumed">Called once the temporary file must no longer be deleted.</param>
    /// <returns>The backup path when one was created.</returns>
    private async Task<string?> SwapInAsync(
        SaveRequest request,
        string tempPath,
        ProgressThrottle throttle,
        long bytesDone,
        int itemsDone,
        Action onTempConsumed)
    {
        throttle.Report(bytesDone, itemsDone, IoPath.GetFileName(request.DestinationPath), isCancellable: false);

        if (!request.ReplaceExisting)
        {
            try
            {
                File.Move(tempPath, request.DestinationPath);
            }
            catch (Exception ex)
            {
                throw IoGuard.Translate(ex, request.DestinationPath);
            }

            return null;
        }

        // Step 5: the session must let go of the file before it can be replaced.
        request.VaultFile?.Close();

        // Step 6: repeat the changed-on-disk check with a fresh stat, then replace.
        RequireUnchanged(request);

        string backupPath = _paths.BackupFileFor(request.DestinationPath);
        IOException? last = null;
        for (int attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            try
            {
                File.Replace(tempPath, request.DestinationPath, backupPath, ignoreMetadataErrors: true);
                return backupPath;
            }
            catch (IOException ex) when (IoGuard.IsReplaceUnsupported(ex))
            {
                return MoveIntoPlace(request.DestinationPath, tempPath, backupPath, onTempConsumed);
            }
            catch (IOException ex) when (IoGuard.IsTransientReplaceFailure(ex))
            {
                // The last attempt falls out of the loop so the documented Locked report below is the
                // one the caller sees; the generic translation would call a lock an IoError.
                last = ex;
                if (attempt < ReplaceAttempts - 1)
                {
                    await Task.Delay(BackoffDelay(attempt), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                throw IoGuard.Translate(ex, request.DestinationPath);
            }
        }

        throw new VaultIoException(
            VaultErrorCode.Locked,
            $"The vault file could not be replaced after {ReplaceAttempts} attempts because another program holds it open.",
            last)
        { OffendingPath = request.DestinationPath };
    }

    /// <summary>
    /// The fallback for a file system that cannot do <see cref="File.Replace(string, string, string)"/>
    /// (<c>ERROR_NOT_SAME_DEVICE</c>, <c>ERROR_INVALID_PARAMETER</c>): move the old file aside, then move
    /// the new one in. Between the two moves the temporary file is the only copy of the new vault, so it
    /// is taken out of the cleanup path first and a failure names every path that holds user data
    /// (FORMAT.md section 8.3).
    /// </summary>
    /// <param name="destinationPath">The vault path being written.</param>
    /// <param name="tempPath">The finished temporary file.</param>
    /// <param name="backupPath">Where the previous version is moved to.</param>
    /// <param name="onTempConsumed">Called once the temporary file must no longer be deleted.</param>
    /// <returns>The backup path.</returns>
    internal static string MoveIntoPlace(string destinationPath, string tempPath, string backupPath, Action onTempConsumed)
    {
        ArgumentNullException.ThrowIfNull(onTempConsumed);

        TryDelete(backupPath);
        File.Move(destinationPath, backupPath);
        onTempConsumed();

        try
        {
            File.Move(tempPath, destinationPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VaultIoException(
                VaultErrorCode.IoError,
                $"The new vault could not be moved into place at {destinationPath}. Nothing was deleted: " +
                $"the version just written is at {tempPath} and the previous version is at {backupPath}.",
                ex)
            { OffendingPath = destinationPath };
        }

        return backupPath;
    }

    /// <summary>Backoff of 100 * 2^n milliseconds plus jitter.</summary>
    /// <param name="attempt">Zero-based attempt number.</param>
    private TimeSpan BackoffDelay(int attempt)
    {
        Span<byte> jitter = stackalloc byte[1];
        _random.Fill(jitter);
        return TimeSpan.FromMilliseconds((100 << attempt) + jitter[0]);
    }

    /// <summary>
    /// FORMAT.md section 8.3 step 8: reopen the file, parse it, compare the entry set and authenticate a
    /// sample of chunks.
    /// </summary>
    /// <param name="request">The save request.</param>
    /// <param name="expectedHeader">The header that was written.</param>
    /// <param name="expectedIndex">The index that was written.</param>
    /// <param name="backupPath">The backup that is kept when verification fails.</param>
    private static FileStat Verify(SaveRequest request, VaultHeader expectedHeader, VaultIndex expectedIndex, string? backupPath)
    {
        string path = request.DestinationPath;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = RandomAccess.GetLength(handle);

            byte[] headerBytes = new byte[VaultHeader.Size];
            FileIo.ReadExactly(handle, 0, headerBytes);
            VaultHeader header = VaultHeader.Parse(headerBytes, length);

            byte[] ciphertext = new byte[header.IndexLength];
            FileIo.ReadExactly(handle, VaultHeader.Size, ciphertext);
            byte[] plaintext = HeaderCipher.DecryptIndex(
                request.DestinationCrypto.IndexKey.Span, header.IndexNonce, ciphertext, header.BuildIndexAad());
            VaultIndex index = IndexSerializer.Deserialize(plaintext);
            CryptographicOperations.ZeroMemory(plaintext);

            if (length != VaultHeader.Size + (2 * header.IndexLength) + index.DataSectionLength)
            {
                throw Failed(path, backupPath, "the length of the new file does not match its index");
            }

            CompareEntries(expectedIndex, index, path, backupPath);
            VerifySample(handle, header, index, request.DestinationCrypto, path, backupPath);

            return FileIo.TryStat(path)
                ?? throw Failed(path, backupPath, "the new file disappeared right after it was written");
        }
        catch (VaultIntegrityException)
        {
            throw;
        }
        catch (ObjectDisposedException ex)
        {
            // Lock() runs on any thread and zeroes the key set. That is a session state change, not a
            // verdict on the file: everywhere else IoGuard turns it into SessionLocked, and calling it
            // SaveVerificationFailed would send the user back to a .bak that is older than their work.
            throw IoGuard.Translate(ex, path);
        }
        catch (VaultOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failed(path, backupPath, ex.Message, ex);
        }
    }

    /// <summary>Compares the entry set that was written with the entry set that was read back.</summary>
    /// <param name="expected">The index that was written.</param>
    /// <param name="actual">The index that was read back.</param>
    /// <param name="path">Destination path, for the message.</param>
    /// <param name="backupPath">Backup path, for the message.</param>
    private static void CompareEntries(VaultIndex expected, VaultIndex actual, string path, string? backupPath)
    {
        if (expected.Entries.Count != actual.Entries.Count ||
            expected.SaveCounter != actual.SaveCounter ||
            expected.DataSectionLength != actual.DataSectionLength)
        {
            throw Failed(path, backupPath, "the index that was read back does not match the index that was written");
        }

        for (int i = 0; i < expected.Entries.Count; i++)
        {
            IndexEntry a = expected.Entries[i];
            IndexEntry b = actual.Entries[i];
            bool same = a.Id == b.Id && a.ParentId == b.ParentId && a.Kind == b.Kind &&
                        string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.Length == b.Length &&
                        a.DataOffset == b.DataOffset && a.ChunkSize == b.ChunkSize &&
                        ((a.BlobId is null && b.BlobId is null) || (a.BlobId is not null && b.BlobId is not null && a.BlobId.AsSpan().SequenceEqual(b.BlobId))) &&
                        ((a.BlobHash is null && b.BlobHash is null) || (a.BlobHash is not null && b.BlobHash is not null && a.BlobHash.AsSpan().SequenceEqual(b.BlobHash)));

            if (!same)
            {
                throw Failed(path, backupPath, $"entry {a.Id} differs between the index that was written and the file on disk");
            }
        }
    }

    /// <summary>Decrypts the first and last chunk of up to three blobs.</summary>
    /// <param name="handle">Handle on the new file.</param>
    /// <param name="header">Header of the new file.</param>
    /// <param name="index">Index of the new file.</param>
    /// <param name="crypto">Keys of the new file.</param>
    /// <param name="path">Destination path, for the message.</param>
    /// <param name="backupPath">Backup path, for the message.</param>
    private static void VerifySample(
        SafeFileHandle handle, VaultHeader header, VaultIndex index, VaultCrypto crypto, string path, string? backupPath)
    {
        var file = new VaultFileHandle(handle);
        int checkedBlobs = 0;
        foreach (IndexEntry entry in index.Entries)
        {
            if (entry.Kind != EntryKind.File || entry.BlobId is null || checkedBlobs >= VerifiedBlobCount)
            {
                continue;
            }

            checkedBlobs++;
            long blobLength = ChunkCipher.BlobLength(entry.Length, entry.ChunkSize);
            var source = new StoredBlobSource(file, header.DataSectionOffset + entry.DataOffset, blobLength);
            using var reader = new BlobReader(source, crypto, entry.BlobId, entry.Length, entry.ChunkSize, entry.Name);

            foreach (uint chunk in new[] { 0u, reader.ChunkCount - 1 })
            {
                byte[] cipher = new byte[reader.CiphertextLengthOf(chunk)];
                byte[] plain = new byte[cipher.Length - ChunkCipher.TagSize];
                try
                {
                    reader.ReadCiphertext(chunk, cipher);
                    reader.DecryptChunk(chunk, cipher, plain);
                }
                catch (VaultException ex)
                {
                    throw Failed(path, backupPath, $"a chunk of entry {entry.Id} did not authenticate after the save", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
        }
    }

    /// <summary>Builds the failure of step 8, naming both the vault and the backup.</summary>
    /// <param name="path">Path of the file that was written.</param>
    /// <param name="backupPath">Path of the retained backup, if any.</param>
    /// <param name="detail">What went wrong.</param>
    /// <param name="inner">The underlying exception, if any.</param>
    private static VaultIntegrityException Failed(string path, string? backupPath, string detail, Exception? inner = null)
    {
        string backup = backupPath is null
            ? "no backup was created because the file is new"
            : $"the previous version is kept at {backupPath}";
        return new VaultIntegrityException(
            VaultErrorCode.SaveVerificationFailed,
            $"The vault at {path} did not verify after saving ({detail}); {backup}.",
            inner)
        { VaultPath = path };
    }

    /// <summary>Opens the temporary file the save writes into.</summary>
    /// <param name="tempPath">Path of the temporary file.</param>
    /// <param name="expectedLength">Length to preallocate.</param>
    private static FileStream OpenTemp(string tempPath, long expectedLength)
    {
        try
        {
            return new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
                PreallocationSize = expectedLength,
            });
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, tempPath);
        }
    }

    /// <summary>Step 0: the destination must not be read-only.</summary>
    /// <param name="request">The save request.</param>
    private static void RequireWritableDestination(SaveRequest request)
    {
        if (!File.Exists(request.DestinationPath))
        {
            return;
        }

        if (!request.ReplaceExisting)
        {
            throw new VaultIoException(
                VaultErrorCode.IoError,
                $"A file already exists at {request.DestinationPath}.")
            { OffendingPath = request.DestinationPath };
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(request.DestinationPath);
        }
        catch (Exception ex)
        {
            throw IoGuard.Translate(ex, request.DestinationPath);
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new VaultIoException(
                VaultErrorCode.ReadOnlyTarget,
                $"The vault file {request.DestinationPath} carries the read-only attribute.")
            { OffendingPath = request.DestinationPath };
        }
    }

    /// <summary>Step 0: the volume must have room for the new file plus head room.</summary>
    /// <param name="destinationPath">Path of the file being written.</param>
    /// <param name="requiredBytes">Bytes that must fit.</param>
    private static void RequireFreeSpace(string destinationPath, long requiredBytes)
    {
        string? directory = IoPath.GetDirectoryName(IoPath.GetFullPath(destinationPath));
        long? free = directory is null ? null : StagingStore.AvailableFreeSpace(directory);
        if (free is null || free >= requiredBytes)
        {
            return;
        }

        throw new VaultResourceException(
            VaultErrorCode.DiskFull,
            $"Saving needs {requiredBytes} bytes on the volume holding {destinationPath}; only {free.Value} are free.")
        {
            RequiredBytes = requiredBytes,
            AvailableBytes = free.Value,
        };
    }

    /// <summary>Steps 1 and 6: the vault file must still look the way the session last saw it.</summary>
    /// <param name="request">The save request.</param>
    private static void RequireUnchanged(SaveRequest request)
    {
        if (!request.ReplaceExisting || request.ExpectedStat is not FileStat expected)
        {
            return;
        }

        FileStat? actual = FileIo.TryStat(request.DestinationPath);
        if (actual is null)
        {
            throw new VaultIoException(
                VaultErrorCode.ChangedOnDisk,
                $"The vault file {request.DestinationPath} no longer exists; it was moved or deleted since it was opened.")
            { OffendingPath = request.DestinationPath };
        }

        if (actual.Value.Length != expected.Length || actual.Value.LastWriteUtc != expected.LastWriteUtc)
        {
            throw new VaultIoException(
                VaultErrorCode.ChangedOnDisk,
                $"The vault file {request.DestinationPath} changed on disk since it was opened or last saved; " +
                "saving now would discard those changes.")
            { OffendingPath = request.DestinationPath };
        }
    }

    /// <summary>A fresh 16-byte blob id.</summary>
    private byte[] NewBlobId()
    {
        byte[] id = new byte[16];
        _random.Fill(id);
        return id;
    }

    /// <summary>A fresh 12-byte nonce.</summary>
    private byte[] NewNonce()
    {
        byte[] nonce = new byte[12];
        _random.Fill(nonce);
        return nonce;
    }

    /// <summary>Clamps a tick count into the range the format allows.</summary>
    /// <param name="ticks">Tick count.</param>
    private static long Clamp(long ticks) => ticks is < 0 or > TreeModel.MaxTicks ? 0 : ticks;

    /// <summary>Deletes a file, ignoring failures.</summary>
    /// <param name="path">File to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary or backup file is swept later; it must never mask the real error.
        }
    }

    /// <summary>Running totals shared with the progress throttle across the whole data section.</summary>
    private sealed class WriteCounters
    {
        /// <summary>Ciphertext bytes written so far.</summary>
        public long Bytes;

        /// <summary>Blobs completed so far.</summary>
        public int Items;
    }

    /// <summary>One blob of the save plan.</summary>
    private sealed class BlobJob
    {
        /// <summary>The entry that owns the blob.</summary>
        public required TreeNode Node { get; init; }

        /// <summary>The content reference that is being written.</summary>
        public required BlobRef Content { get; init; }

        /// <summary>True when the ciphertext is copied byte for byte.</summary>
        public required bool Verbatim { get; init; }

        /// <summary>Blob id in the new file.</summary>
        public required byte[] DestinationBlobId { get; init; }

        /// <summary>Offset inside the data section.</summary>
        public required long Offset { get; init; }

        /// <summary>Ciphertext length.</summary>
        public required long BlobLength { get; init; }

        /// <summary>The index entry this blob belongs to, filled in while the index is built.</summary>
        public IndexEntry? Entry { get; set; }
    }
}
