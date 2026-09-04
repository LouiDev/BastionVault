using System.Globalization;
using BastionVault.App.Converters;
using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.App.Views;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests;

/// <summary>
/// Regressions for the review findings that have no better home: each one pins the behaviour the
/// fix introduced, so the old behaviour cannot come back unnoticed.
/// </summary>
public sealed class ReviewFixTests
{
    // ── ux-01 / ux-02 / ux-05: the command bar is responsive ────────────────────

    [Fact]
    public void TheCommandBarDropsLabelsBeforeItLosesTheSearchBoxOrTheRightHandGroup()
    {
        // With every label drawn the bar asks for more than the window's declared 880 DIP
        // minimum, and Save, Verify and Lock were simply clipped off the end while the search
        // field was clipped to nothing yet stayed focusable.
        Assert.True(CommandBarView.TierFor(1400).Labels);
        Assert.True(CommandBarView.TierFor(1400).Search);

        Assert.False(CommandBarView.TierFor(1000).Labels);
        Assert.True(CommandBarView.TierFor(1000).Search);

        // The declared minimum window width still shows the search field.
        Assert.False(CommandBarView.TierFor(880).Labels);
        Assert.True(CommandBarView.TierFor(880).Search);

        // Narrower than the field can be drawn at, it is collapsed rather than left invisible
        // but focusable.
        Assert.False(CommandBarView.TierFor(600).Search);
    }

    // ── crypto-02: auto-lock never fires under a running operation ──────────────

    [Fact]
    public void AutoLockWaitsForARunningOperationAndThenLocks()
    {
        var idle = Substitute.For<IIdleMonitor>();
        var system = Substitute.For<ISystemEvents>();
        var session = Substitute.For<IVaultSession>();
        var settings = new MemorySettings();
        var log = new MemoryLog();

        session.IsLocked.Returns(false);
        session.IsBusy.Returns(true);

        using var controller = new AutoLockController(idle, system, settings, log);
        controller.Session = session;

        idle.IdleThresholdReached += Raise.Event<EventHandler>(idle, EventArgs.Empty);

        session.DidNotReceive().Lock();
        Assert.Equal(AutoLockReason.Idle, controller.Deferred);

        session.IsBusy.Returns(false);
        controller.ResumeDeferred();

        session.Received(1).Lock();
        Assert.Null(controller.Deferred);
    }

    [Fact]
    public void LogOffLocksEvenWhileTheSessionIsBusy()
    {
        var idle = Substitute.For<IIdleMonitor>();
        var system = Substitute.For<ISystemEvents>();
        var session = Substitute.For<IVaultSession>();

        session.IsLocked.Returns(false);
        session.IsBusy.Returns(true);

        using var controller = new AutoLockController(idle, system, new MemorySettings(), new MemoryLog());
        controller.Session = session;

        system.SessionEnding += Raise.Event<EventHandler>(system, EventArgs.Empty);

        session.Received(1).Lock();
    }

    // ── wpf-05: no path ever reaches the log ────────────────────────────────────

    [Fact]
    public void ExceptionMessagesAreScrubbedOfPaths()
    {
        Assert.Equal(
            "Could not find file '<path>'.",
            FileLog.Scrub("Could not find file 'C:" + Sep + "Users" + Sep + "ada" + Sep + "vault.bastion'."));

        Assert.Equal(
            "There is no file at <path>",
            FileLog.Scrub("There is no file at C:" + Sep + "vaults" + Sep + "demo.bastion."));

        Assert.Equal(
            "Access to the path '<path>' is denied.",
            FileLog.Scrub("Access to the path '" + Sep + Sep + "server" + Sep + "share" + Sep + "v.bastion' is denied."));

        // An in-vault path is exactly what an export failure carries.
        Assert.Equal(
            "Could not write <path>",
            FileLog.Scrub("Could not write " + Sep + "Documents" + Sep + "2026" + Sep + "notes.txt"));
    }

    [Theory]
    [InlineData("Vault locked automatically (Idle).")]
    [InlineData("Imported 4 items and/or folders")]
    [InlineData("3 items / 4 selected")]
    public void OrdinaryMessagesAreLeftAlone(string message)
    {
        Assert.Equal(message, FileLog.Scrub(message));
    }

    // ── wpf-04: a failed settings write never reaches the crash handler ─────────

    [Fact]
    public void SavingSettingsToAnImpossiblePathDoesNotThrow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BastionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string blocked = Path.Combine(directory, "settings.json");

        try
        {
            // A directory where the settings file should be: every write fails.
            Directory.CreateDirectory(blocked);

            var log = new MemoryLog();
            var service = new JsonSettingsService(blocked, log);
            service.Current.PreviewEnabled = false;

            service.Save();

            Assert.False(service.Current.PreviewEnabled);
            Assert.Contains(log.Lines, l => l.Contains("settings", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── wpf-13: the 80 ms floor between progress deliveries ─────────────────────

    [Fact]
    public void ASecondProgressReportIsHeldForTheMinimumInterval()
    {
        var dispatcher = new ManualDispatcher();
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(dispatcher, seen.Add);

        progress.Report(1);
        Assert.Null(dispatcher.LastDelay);
        dispatcher.Drain();

        progress.Report(2);

        Assert.Equal(1, dispatcher.DelayedCount);
        Assert.NotNull(dispatcher.LastDelay);
        Assert.InRange(dispatcher.LastDelay!.Value, TimeSpan.Zero, ThrottledProgress<int>.MinimumInterval);

        dispatcher.Drain();
        Assert.Equal([1, 2], seen);
    }

    // ── ux-08: the strength sentence stays grammatical ──────────────────────────

    [Fact]
    public void AnOpenEndedCrackTimeDoesNotGetTheAboutLeadIn()
    {
        string sentence = PasswordStrength.Sentence(
            PasswordStrength.Estimate("correct horse battery staple mountain lantern 42!"),
            KdfParameters.Default,
            "Standard");

        Assert.DoesNotContain("about longer than", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("about less than", sentence, StringComparison.Ordinal);
    }

    // ── ux-12: one clock convention in the Modified column ──────────────────────

    [Fact]
    public void EveryRelativeDateBranchUsesTheCultureShortTime()
    {
        var culture = new CultureInfo("en-US");
        var now = new DateTimeOffset(2026, 9, 3, 20, 0, 0, TimeSpan.Zero);

        string yesterday = RelativeDateConverter.Format(now.AddDays(-1), now, culture);
        string thisWeek = RelativeDateConverter.Format(now.AddDays(-3), now, culture);
        string thisYear = RelativeDateConverter.Format(now.AddDays(-40), now, culture);

        // en-US short time carries AM/PM; a hard-coded HH:mm branch would not.
        Assert.True(HasMeridiem(yesterday), yesterday);
        Assert.True(HasMeridiem(thisWeek), thisWeek);
        Assert.True(HasMeridiem(thisYear), thisYear);

        static bool HasMeridiem(string text) =>
            text.Contains("AM", StringComparison.Ordinal) || text.Contains("PM", StringComparison.Ordinal);
    }

    // ── ux-15: panic mode gives away no name lengths ────────────────────────────

    [Fact]
    public void MaskedNamesAreAllTheSameWidth()
    {
        string shortName = EntryItemViewModel.MaskName("a.txt");
        string longName = EntryItemViewModel.MaskName("Quarterly report 2026 final FINAL.xlsx");

        Assert.Equal(shortName, longName);
        Assert.Equal(EntryItemViewModel.MaskLength, shortName.Length);
    }

    /// <summary>
    /// The crash handlers write their line before they do anything else, so <see cref="FileLog"/>
    /// must be incapable of throwing: a logger that throws inside a crash handler turns a reported
    /// crash into a silent one - the process is gone and nothing on disk says why. A directory that
    /// cannot be opened, and a message that cannot be scrubbed inside the regex budget, both make
    /// the log go quiet rather than raise.
    /// </summary>
    [Fact]
    public void TheLogGoesQuietRatherThanThrowWhateverItIsHandedOrPointedAt()
    {
        string impossible = Path.Combine(Path.GetTempPath(), "BastionTests", "bad" + Sep + Sep + "|dir");

        FileLog log = new(impossible);
        log.Info("a line nobody can write");
        log.Warn("another", new InvalidOperationException("no"));
        log.Error("and a third", new InvalidOperationException("no"));
        log.Dispose();

        // A working log takes an exception whose message is nothing but path-shaped text.
        string directory = Path.Combine(Path.GetTempPath(), "BastionTests", Guid.NewGuid().ToString("N"));

        try
        {
            using var real = new FileLog(directory);
            real.Error("crash", new InvalidOperationException("C:" + Sep + new string('a', 20_000)));

            string written = string.Concat(Directory.EnumerateFiles(directory).Select(File.ReadAllText));
            Assert.Contains("crash", written, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('a', 40), written, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>The Windows separator, kept out of the literals so the test file has no escapes.</summary>
    private static string Sep => new(Path.DirectorySeparatorChar, 1);
}
