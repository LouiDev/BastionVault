using System.Security.Cryptography;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;

namespace BastionVault.App.Services;

/// <summary>What a duplicate scan found.</summary>
/// <param name="Groups">Groups of identical files, largest first; empty when every file is different.</param>
/// <param name="FilesScanned">How many files the scan looked at, including the ones it could rule out by length.</param>
/// <param name="BytesHashed">How many plaintext bytes had to be read to decide.</param>
public sealed record DuplicateScan(IReadOnlyList<DuplicateGroup> Groups, int FilesScanned, long BytesHashed);

/// <summary>
/// Finds files with identical contents without any help from the index: the format keeps a hash per
/// blob, but it is not on the public surface and a copy inside the vault gets a fresh blob anyway. So
/// the scan groups files by length first, which rules out almost everything for free, and only then
/// reads the remaining candidates through the session's decrypting stream and hashes them. Nothing
/// is kept but the digests; every read buffer is zeroed on the way out.
/// </summary>
public static class DuplicateFinder
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Scans the whole vault.</summary>
    /// <param name="session">The open session.</param>
    /// <param name="progress">Progress, reported per file and per megabyte read.</param>
    /// <param name="ct">Cancellation token; honoured between reads.</param>
    public static async Task<DuplicateScan> FindAsync(IVaultSession session, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<WalkedFile> files = [.. VaultTreeWalker.EnumerateFiles(session, EntryId.Root, ct)];

        // Only a length that occurs more than once can hide a duplicate. Empty files are left out:
        // they are all identical, and nobody wants a list of them.
        List<List<WalkedFile>> candidates =
        [
            .. files
                .Where(f => f.Info.Length > 0)
                .GroupBy(f => f.Info.Length)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList()),
        ];

        long bytesTotal = candidates.Sum(g => g.Sum(f => f.Info.Length));
        int itemsTotal = candidates.Sum(g => g.Count);
        long bytesDone = 0;
        int itemsDone = 0;
        progress?.Report(new VaultProgress(VaultOperation.Verify, 0, bytesTotal, 0, itemsTotal, null, true));

        var groups = new List<DuplicateGroup>();
        byte[] buffer = new byte[BufferSize];
        try
        {
            foreach (List<WalkedFile> sameLength in candidates)
            {
                var byDigest = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
                foreach (WalkedFile file in sameLength)
                {
                    ct.ThrowIfCancellationRequested();
                    string path = VaultTreeWalker.Join(file.FolderPath, file.Info.Name);
                    progress?.Report(new VaultProgress(VaultOperation.Verify, bytesDone, bytesTotal, itemsDone, itemsTotal, file.Info.Name, true));

                    string digest = await DigestAsync(session, file.Info.Id, buffer, ct).ConfigureAwait(false);
                    bytesDone += file.Info.Length;
                    itemsDone++;

                    if (!byDigest.TryGetValue(digest, out List<DuplicateFile>? list))
                    {
                        list = [];
                        byDigest[digest] = list;
                    }

                    list.Add(new DuplicateFile(file.Info.Id, file.Info.ParentId, path));
                }

                foreach (List<DuplicateFile> identical in byDigest.Values)
                {
                    if (identical.Count > 1)
                    {
                        groups.Add(new DuplicateGroup(sameLength[0].Info.Length, identical));
                    }
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        progress?.Report(new VaultProgress(VaultOperation.Verify, bytesTotal, bytesTotal, itemsTotal, itemsTotal, null, true));
        groups.Sort((a, b) => b.Length.CompareTo(a.Length) != 0
            ? b.Length.CompareTo(a.Length)
            : string.CompareOrdinal(a.Files[0].Path, b.Files[0].Path));

        return new DuplicateScan(groups, files.Count, bytesDone);
    }

    private static async Task<string> DigestAsync(IVaultSession session, EntryId file, byte[] buffer, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream stream = await session.OpenReadAsync(file, ct).ConfigureAwait(false);

        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
