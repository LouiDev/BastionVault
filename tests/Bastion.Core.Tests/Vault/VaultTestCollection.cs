namespace Bastion.Core.Tests.Vault;

/// <summary>
/// The one collection of this folder that must not run beside anything else: the resource-limit tests.
/// </summary>
/// <remarks>
/// <c>LimitsTests</c> asserts that a header claiming absurd parameters is answered without touching the
/// key derivation, and it proves that by the clock - each of those answers has to come back in well
/// under a second. A saturated thread pool would make those budgets meaningless, so these tests get the
/// machine to themselves. Everything else in this folder runs in parallel with the rest of the suite;
/// the one test that used to make that unsafe (a cancelled save whose <c>Progress&lt;T&gt;</c> callback
/// landed after the save had committed and then crashed the test host) now cancels inline through
/// <see cref="SynchronousProgress"/>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VaultTestCollection
{
    /// <summary>Name of the collection; only the resource-limit tests carry it.</summary>
    public const string Name = "Resource limit tests";
}
