namespace BastionVault.Core;

/// <summary>One integrity problem found by <see cref="IVaultSession.VerifyAsync"/>.</summary>
/// <param name="Id">Entry the failure belongs to.</param>
/// <param name="VaultPath">In-vault path of that entry.</param>
/// <param name="ChunkIndex">Chunk that failed, or <see langword="null"/> for a whole-blob failure.</param>
/// <param name="Detail">Human-readable description of the failure.</param>
public sealed record VerifyFailure(EntryId Id, string VaultPath, uint? ChunkIndex, string Detail);

/// <summary>Outcome of a full verification pass.</summary>
/// <param name="FilesChecked">Number of files whose blob was authenticated.</param>
/// <param name="BytesChecked">Ciphertext bytes read.</param>
/// <param name="Elapsed">Wall-clock duration of the pass.</param>
/// <param name="LayoutOk">True when the index, the blob tiling and the length equation of FORMAT.md §1 are consistent.</param>
/// <param name="Failures">Every failure found; verification is continue-on-error.</param>
public sealed record VerifyReport(
    int FilesChecked,
    long BytesChecked,
    TimeSpan Elapsed,
    bool LayoutOk,
    IReadOnlyList<VerifyFailure> Failures)
{
    /// <summary>True when the layout is consistent and no blob failed: "every byte is accounted for".</summary>
    public bool IsClean => LayoutOk && Failures.Count == 0;
}
