using BastionVault.Core.Crypto;
using BastionVault.Core.Format;

namespace BastionVault.Core.Tests.Vault;

/// <summary>
/// The two halves of the KDF memory story: the pre-flight as a question the UI can ask before the button is
/// pressed, and the allocation that fails anyway on a machine that passed it. API.md rule 5 says no raw
/// exception leaves Core; an <see cref="OutOfMemoryException"/> from the pinned Argon2 block array used
/// to be the one that did.
/// </summary>
[Collection(VaultTestCollection.Name)]
public sealed class KdfMemoryTests
{
    [Fact]
    public void The_preflight_answers_with_the_figures_the_refusal_would_carry()
    {
        KdfPreflightResult verdict = KdfPreflight.Check(KdfParameters.Default);

        Assert.Equal(KdfParameters.Default.MemoryBytes, verdict.RequiredBytes);

        if (verdict.InstalledBytes == 0)
        {
            // Nothing could be measured: FORMAT.md section 3.1 step 9 lets such a machine through.
            Assert.True(verdict.Fits);
            Assert.Equal(0, verdict.BudgetBytes);
            return;
        }

        long budget = (long)(verdict.InstalledBytes * VaultLimits.KdfMemoryFractionOfInstalled);
        Assert.Equal(budget, verdict.BudgetBytes);
        Assert.Equal(verdict.RequiredBytes <= budget, verdict.Fits);
        Assert.Equal(KdfPreflight.InstalledPhysicalMemoryBytes(), verdict.InstalledBytes);
    }

    [Fact]
    public void The_preflight_and_the_refusal_agree()
    {
        var huge = new KdfParameters(VaultLimits.MaxKdfMemoryKiB, 3, 4);
        KdfPreflightResult verdict = KdfPreflight.Check(huge);

        if (verdict.Fits)
        {
            Core.Session.Credentials.PreflightMemory(huge);
            return;
        }

        var refusal = Assert.Throws<VaultResourceException>(() => Core.Session.Credentials.PreflightMemory(huge));
        Assert.Equal(VaultErrorCode.ResourceLimit, refusal.Code);
        Assert.Equal(verdict.RequiredBytes, refusal.RequiredBytes);
        Assert.Equal(verdict.BudgetBytes, refusal.AvailableBytes);
    }

    [Fact]
    public void The_largest_fitting_preset_is_the_most_expensive_one_the_preflight_lets_through()
    {
        KdfPreset? largest = KdfPreflight.LargestFittingPreset();

        foreach (KdfPreset preset in new[] { KdfPreset.Strong, KdfPreset.Standard, KdfPreset.Fast })
        {
            bool fits = KdfPreflight.Check(KdfParameters.FromPreset(preset)).Fits;
            if (fits)
            {
                Assert.Equal(preset, largest);
                return;
            }
        }

        Assert.Null(largest);
    }

    [Fact]
    public void The_preflight_rejects_a_null_parameter_set()
    {
        Assert.Throws<ArgumentNullException>(() => KdfPreflight.Check(null!));
    }

    /// <summary>
    /// A vault whose header passes the installed-memory pre-flight can still fail to allocate its Argon2
    /// blocks. That failure must leave Core as <see cref="VaultErrorCode.ResourceLimit"/> with the figures
    /// a UI can show, never as the raw <see cref="OutOfMemoryException"/> the App's crash handler used to
    /// report as an unexpected error.
    /// </summary>
    [Fact]
    public async Task An_allocation_failure_inside_the_kdf_leaves_core_as_a_resource_limit()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);
        vault.Write(vault.Copy());
        var kdf = new OutOfMemoryKeyDerivation();

        VaultException error = await Assert.ThrowsAnyAsync<VaultException>(async () =>
        {
            await using IVaultSession session = await vault.OpenTargetAsync(kdf: kdf);
        });

        VaultAssert.Failure(error, VaultErrorCode.ResourceLimit, "the Argon2 block array could not be allocated");
        var resource = (VaultResourceException)error;
        Assert.IsType<OutOfMemoryException>(resource.InnerException);
        Assert.Equal(kdf.Requested!.MemoryBytes, resource.RequiredBytes);
        Assert.Contains("try again", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(vault.TargetPath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_allocation_failure_while_unlocking_a_locked_session_is_translated_the_same_way()
    {
        using TamperVault vault = await TamperVault.CreateAsync(withBigFile: false);
        vault.Write(vault.Copy());
        var kdf = new OutOfMemoryKeyDerivation { FailFromCall = 2 };

        await using IVaultSession session = await vault.OpenTargetAsync(kdf: kdf);
        session.Lock();

        using Passphrase password = Passphrase.FromString(TamperVault.Password);
        var error = await Assert.ThrowsAsync<VaultResourceException>(
            () => session.UnlockAsync(password, null, null, CancellationToken.None));

        Assert.Equal(VaultErrorCode.ResourceLimit, error.Code);
        Assert.True(session.IsLocked, "a failed unlock leaves the session locked");
    }

    /// <summary>
    /// A key derivation whose block allocation fails. The first <see cref="FailFromCall"/> - 1 calls derive the
    /// real key so the vault can be opened first when a test needs a session.
    /// </summary>
    private sealed class OutOfMemoryKeyDerivation : IKeyDerivation
    {
        private int _calls;

        /// <summary>The 1-based call from which every derivation fails; 1 by default.</summary>
        public int FailFromCall { get; init; } = 1;

        /// <summary>Parameters of the call that failed.</summary>
        public KdfParameters? Requested { get; private set; }

        /// <inheritdoc />
        public byte[] DeriveArgon2id(
            ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, KdfParameters parameters, int tagLength, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call < FailFromCall)
            {
                return Argon2.Instance.DeriveArgon2id(password, salt, parameters, tagLength, ct);
            }

            Requested = parameters;
            throw new OutOfMemoryException("Array dimensions exceeded supported range.");
        }
    }
}
