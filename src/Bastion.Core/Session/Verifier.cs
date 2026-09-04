using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Bastion.Core.Crypto;
using Bastion.Core.Format;

namespace Bastion.Core.Session;

/// <summary>The layout of the vault file as it currently sits on disk, captured at open and after a save.</summary>
/// <param name="IndexLength">Encrypted index length from the header.</param>
/// <param name="DataSectionLength">Data section length from the index.</param>
/// <param name="DataPaddingLength">Trailing random bytes inside the data section.</param>
/// <param name="Blobs">Offset and ciphertext length of every stored blob, in index order.</param>
/// <param name="FileLength">Length the file had when the layout was captured.</param>
internal sealed record StoredLayout(
    long IndexLength,
    long DataSectionLength,
    long DataPaddingLength,
    IReadOnlyList<(long Offset, long Length)> Blobs,
    long FileLength);

/// <summary>
/// Reads every blob, authenticates every chunk and compares every commitment hash, so that a clean
/// report really means "every byte is accounted for" (FORMAT.md section 2.8).
/// </summary>
internal sealed class Verifier
{
    private readonly VaultSession _session;

    /// <summary>Creates a verifier for one call.</summary>
    /// <param name="session">The session that owns the tree and the keys.</param>
    public Verifier(VaultSession session) => _session = session;

    /// <summary>Runs a full verification pass.</summary>
    /// <param name="files">Every file entry of the tree.</param>
    /// <param name="layout">The layout captured at open or after the last save.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    public VerifyReport Run(IReadOnlyList<TreeNode> files, StoredLayout layout, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var failures = new List<VerifyFailure>();
        bool layoutOk = CheckLayout(layout, failures);

        long totalBytes = 0;
        foreach (TreeNode file in files)
        {
            totalBytes += file.Content?.BlobLength ?? 0;
        }

        var throttle = new ProgressThrottle(progress, VaultOperation.Verify, totalBytes, files.Count);
        throttle.Start();

        long bytesChecked = 0;
        int checkedFiles = 0;
        foreach (TreeNode file in files)
        {
            ct.ThrowIfCancellationRequested();
            bytesChecked += VerifyBlob(file, failures, throttle, bytesChecked, checkedFiles, ct);
            checkedFiles++;
        }

        throttle.Complete(bytesChecked, checkedFiles);
        return new VerifyReport(
            checkedFiles,
            bytesChecked,
            Stopwatch.GetElapsedTime(started),
            layoutOk,
            failures);
    }

    /// <summary>Checks the length equation of FORMAT.md section 1 and the blob tiling of section 4.6 rule 9.</summary>
    /// <param name="layout">The captured layout.</param>
    /// <param name="failures">List that receives the findings.</param>
    private bool CheckLayout(StoredLayout layout, List<VerifyFailure> failures)
    {
        bool ok = true;

        FileStat? stat = FileIo.TryStat(_session.Path);
        long expected = VaultHeader.Size + (2 * layout.IndexLength) + layout.DataSectionLength;
        if (stat is null || stat.Value.Length != expected)
        {
            failures.Add(new VerifyFailure(
                EntryId.Root,
                VaultPath.Format([]),
                null,
                $"The vault file is {stat?.Length.ToString() ?? "missing"} bytes long; the header and index require exactly {expected}."));
            ok = false;
        }

        var sorted = new List<(long Offset, long Length)>(layout.Blobs);
        sorted.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        long cursor = 0;
        foreach ((long offset, long length) in sorted)
        {
            if (offset != cursor)
            {
                failures.Add(new VerifyFailure(
                    EntryId.Root,
                    VaultPath.Format([]),
                    null,
                    $"The data section does not tile: a blob starts at {offset} while the previous one ends at {cursor}."));
                ok = false;
                break;
            }

            cursor = offset + length;
        }

        long content = layout.DataSectionLength - layout.DataPaddingLength;
        if (ok && cursor != content)
        {
            failures.Add(new VerifyFailure(
                EntryId.Root,
                VaultPath.Format([]),
                null,
                $"The blobs cover {cursor} bytes but the content area of the data section is {content} bytes."));
            ok = false;
        }

        return ok;
    }

    /// <summary>Authenticates every chunk of one blob and compares its commitment hash.</summary>
    /// <param name="file">The file entry.</param>
    /// <param name="failures">List that receives the findings.</param>
    /// <param name="throttle">Progress sink.</param>
    /// <param name="bytesBefore">Bytes verified before this blob.</param>
    /// <param name="itemsBefore">Blobs verified before this one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of ciphertext bytes read.</returns>
    private long VerifyBlob(
        TreeNode file, List<VerifyFailure> failures, ProgressThrottle throttle, long bytesBefore, int itemsBefore, CancellationToken ct)
    {
        BlobRef? content = file.Content;
        string vaultPath = TreeModel.FormatPath(file);
        if (content is null)
        {
            failures.Add(new VerifyFailure(new EntryId(file.Id), vaultPath, null, "The entry has no content."));
            return 0;
        }

        long read = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using BlobReader reader = _session.OpenBlobReader(content, vaultPath);

        byte[] cipher = ArrayPool<byte>.Shared.Rent(reader.MaxChunkCiphertextLength);
        byte[] plain = ArrayPool<byte>.Shared.Rent(Math.Max(reader.MaxChunkPlaintextLength, 1));
        try
        {
            for (uint chunk = 0; chunk < reader.ChunkCount; chunk++)
            {
                ct.ThrowIfCancellationRequested();
                int cipherLength = reader.CiphertextLengthOf(chunk);
                try
                {
                    reader.ReadCiphertext(chunk, cipher.AsSpan(0, cipherLength));
                }
                catch (VaultException ex)
                {
                    failures.Add(new VerifyFailure(new EntryId(file.Id), vaultPath, chunk, ex.Message));
                    return read;
                }

                hash.AppendData(cipher.AsSpan(0, cipherLength));
                read += cipherLength;

                try
                {
                    reader.DecryptChunk(chunk, cipher.AsSpan(0, cipherLength), plain.AsSpan(0, cipherLength - ChunkCipher.TagSize));
                }
                catch (VaultIntegrityException ex)
                {
                    failures.Add(new VerifyFailure(new EntryId(file.Id), vaultPath, chunk, ex.Message));
                    return read;
                }

                throttle.Report(bytesBefore + read, itemsBefore, file.Name);
            }

            byte[] actual = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actual, content.BlobHash))
            {
                failures.Add(new VerifyFailure(
                    new EntryId(file.Id), vaultPath, null,
                    "The stored content hash does not match the bytes on disk."));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cipher);
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
        }

        return read;
    }
}
