using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;

namespace BastionVault.App.Tests;

/// <summary>
/// Single-instance identity (#20): a vault that exists is one vault however its path is spelled, because
/// the lock is keyed on the file id; a vault that does not exist yet is keyed on its path alone.
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BastionSingleInstance", Guid.NewGuid().ToString("N"));
    private readonly string _vault;

    public SingleInstanceTests()
    {
        Directory.CreateDirectory(_root);
        _vault = Path.Combine(_root, "one.bastion");
        File.WriteAllBytes(_vault, new byte[160]);
    }

    /// <summary>The same file through the extended-length prefix, which the path spelling alone would not unify.</summary>
    private string Alias => @"\\?\" + _vault;

    [Fact]
    public void An_existing_file_has_the_same_identity_under_every_spelling_of_its_path()
    {
        string? direct = SingleInstance.FileIdentity(_vault);
        string? alias = SingleInstance.FileIdentity(Alias);
        string? cased = SingleInstance.FileIdentity(_vault.ToUpperInvariant());

        Assert.NotNull(direct);
        Assert.Equal(direct, alias);
        Assert.Equal(direct, cased);
    }

    [Fact]
    public void Two_different_files_have_different_identities()
    {
        string other = Path.Combine(_root, "two.bastion");
        File.WriteAllBytes(other, new byte[160]);

        Assert.NotEqual(SingleInstance.FileIdentity(_vault), SingleInstance.FileIdentity(other));
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_has_no_identity_and_is_named_by_its_path_alone()
    {
        string missing = Path.Combine(_root, "not-yet.bastion");

        Assert.Null(SingleInstance.FileIdentity(missing));
        Assert.Single(SingleInstance.NamesFor(missing));
    }

    [Fact]
    public void An_existing_file_is_named_by_its_id_first_and_its_path_second()
    {
        IReadOnlyList<string> direct = SingleInstance.NamesFor(_vault);
        IReadOnlyList<string> alias = SingleInstance.NamesFor(Alias);

        Assert.Equal(2, direct.Count);
        Assert.Equal(2, alias.Count);
        Assert.Equal(direct[0], alias[0]);        // one file, one id name
        Assert.NotEqual(direct[1], alias[1]);     // two spellings, two path names
        Assert.All(direct, name => Assert.StartsWith("BastionVault.Vault.", name, StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_file_under_another_spelling_cannot_be_acquired_by_another_process()
    {
        var log = new MemoryLog();
        var instance = new SingleInstance(onFocusRequested: null, log);

        // A named mutex is re-entrant for its owning thread, so the "other process" is another thread.
        using var holder = OtherThread.Acquire(instance, _vault);
        Assert.NotNull(holder.Lock);

        Assert.Null(instance.TryAcquireVault(Alias));
        Assert.Null(instance.TryAcquireVault(_vault));
    }

    [Fact]
    public void A_released_lock_can_be_taken_again()
    {
        var instance = new SingleInstance();

        using (var holder = OtherThread.Acquire(instance, _vault))
        {
            Assert.NotNull(holder.Lock);
        }

        using IDisposable? again = instance.TryAcquireVault(Alias);
        Assert.NotNull(again);
    }

    [Fact]
    public void A_lock_taken_before_the_file_existed_still_refuses_an_opener_that_finds_the_file()
    {
        // The create flow: the path name is taken first, then the file appears. An opener computes the id
        // name (free) and the path name (held), and must be refused on the second.
        string created = Path.Combine(_root, "created.bastion");
        var instance = new SingleInstance();

        using var creator = OtherThread.Acquire(instance, created);
        Assert.NotNull(creator.Lock);
        File.WriteAllBytes(created, new byte[160]);

        Assert.Null(instance.TryAcquireVault(created));
    }

    [Fact]
    public void Re_acquiring_after_a_create_adds_the_id_name_on_the_same_thread()
    {
        // What the shell does after CreateAsync: take the lock again now that the file exists, then drop
        // the path-only lock. The mutex is re-entrant for the owning thread, so this never blocks.
        string created = Path.Combine(_root, "created.bastion");
        var instance = new SingleInstance();

        IDisposable? pathOnly = instance.TryAcquireVault(created);
        Assert.NotNull(pathOnly);
        File.WriteAllBytes(created, new byte[160]);

        IDisposable? upgraded = instance.TryAcquireVault(created);
        Assert.NotNull(upgraded);
        pathOnly.Dispose();

        // Still held under both names: an alias opener in another process is refused ...
        using (var opener = OtherThread.Acquire(instance, @"\\?\" + created))
        {
            Assert.Null(opener.Lock);
        }

        // ... until the upgraded lock goes.
        upgraded.Dispose();
        using var free = OtherThread.Acquire(instance, @"\\?\" + created);
        Assert.NotNull(free.Lock);
    }

    /// <summary>
    /// Takes a lock on a dedicated thread and holds it until disposed, standing in for a second process:
    /// mutex ownership is per thread, so the test thread's own attempts are refused exactly as another
    /// process's would be.
    /// </summary>
    private sealed class OtherThread : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        private OtherThread(SingleInstance instance, string path)
        {
            using var acquired = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                Lock = instance.TryAcquireVault(path);
                acquired.Set();
                _release.Wait();
                Lock?.Dispose();
            })
            { IsBackground = true };
            _thread.Start();
            acquired.Wait(TimeSpan.FromSeconds(10));
        }

        /// <summary>The lock the other thread got, or <see langword="null"/> when it was refused.</summary>
        public IDisposable? Lock { get; private set; }

        public static OtherThread Acquire(SingleInstance instance, string path) => new(instance, path);

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(10));
            _release.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a run.
        }
    }
}
