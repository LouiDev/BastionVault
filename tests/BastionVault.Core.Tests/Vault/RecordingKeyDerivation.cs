using BastionVault.Core.Crypto;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// A key-derivation seam that never derives anything. It records what it was asked for and returns a
/// fixed tag, so a test can prove where in FORMAT.md section 3.1 a header was rejected: before step 9
/// the KDF is never reached, and a header that survives the pre-flight reaches it with exactly the
/// parameters the file declares - without spending a single byte of Argon2 memory.
/// </summary>
internal sealed class RecordingKeyDerivation : IKeyDerivation
{
    private readonly List<KdfParameters> _calls = [];

    /// <summary>Every parameter set the reader asked to derive, in order.</summary>
    public IReadOnlyList<KdfParameters> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <inheritdoc />
    public byte[] DeriveArgon2id(
        ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int tagLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ct.ThrowIfCancellationRequested();

        lock (_calls)
        {
            _calls.Add(parameters);
        }

        // A deterministic, obviously wrong tag: the key unwrap that follows must fail authentication.
        byte[] tag = new byte[tagLength];
        Array.Fill(tag, (byte)0xAB);
        return tag;
    }
}
