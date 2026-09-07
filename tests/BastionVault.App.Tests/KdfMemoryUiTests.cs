using BastionVault.App.Services;
using BastionVault.App.Tests.Fakes;
using BastionVault.App.ViewModels;
using BastionVault.App.ViewModels.Dialogs;
using BastionVault.Core;
using NSubstitute;

namespace BastionVault.App.Tests;

/// <summary>
/// The KDF memory pre-flight surfaced before the click (unlock card, preset pickers) and the way forward
/// after a refusal that arrived anyway. Core decides; these tests only check that the decision is shown
/// at the right moment and that the retry is offered where retrying can help.
/// </summary>
public sealed class KdfMemoryUiTests
{
    private const long OneGiB = 1024L * 1024 * 1024;

    private readonly IFileDialogService _files = Substitute.For<IFileDialogService>();
    private readonly IKdfEstimator _estimator = Substitute.For<IKdfEstimator>();
    private readonly MemoryLog _log = new();

    /// <summary>A PC with 1 GiB installed: budget 768 MiB, so Fast and Standard fit and Strong does not.</summary>
    private static FakeKdfPreflight SmallMachine() => new() { InstalledBytes = OneGiB };

    [Fact]
    public void The_unlock_card_warns_and_takes_the_button_away_when_the_vault_cannot_be_served_here()
    {
        var card = new UnlockViewModel(_files, SmallMachine(), _log);

        card.Configure(@"C:\vaults\big.bastion", KdfParameters.FromPreset(KdfPreset.Strong), null);
        card.HasPassword = true;

        Assert.True(card.HasMemoryWarning);
        Assert.Contains(OperationViewModel.FormatBytes(OneGiB), card.MemoryWarning, StringComparison.Ordinal);
        Assert.Contains("refused", card.MemoryWarning, StringComparison.Ordinal);
        Assert.False(card.CanSubmit, "pressing Unlock could only produce the refusal the card already states");

        // The figure the vault needs is still stated; the warning is in addition to it, not instead of it.
        Assert.Contains("needs 1024 MiB RAM", card.HeaderLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unlock_card_stays_quiet_when_the_vault_fits()
    {
        var card = new UnlockViewModel(_files, SmallMachine(), _log);

        card.Configure(@"C:\vaults\ok.bastion", KdfParameters.FromPreset(KdfPreset.Standard), null);
        card.HasPassword = true;

        Assert.False(card.HasMemoryWarning);
        Assert.True(card.CanSubmit);
    }

    [Fact]
    public void The_unlock_card_asks_nothing_when_the_header_is_not_known_yet()
    {
        var preflight = SmallMachine();
        var card = new UnlockViewModel(_files, preflight, _log);

        card.Configure(@"C:\vaults\unknown.bastion", null, null);

        Assert.Empty(preflight.Asked);
        Assert.False(card.HasMemoryWarning);
    }

    [Fact]
    public async Task A_memory_refusal_after_the_click_keeps_the_card_names_the_figures_and_offers_a_retry()
    {
        var card = new UnlockViewModel(_files, new FakeKdfPreflight(), _log);
        card.Configure(@"C:\vaults\busy.bastion", KdfParameters.Default, null);
        card.HasPassword = true;
        bool selectRequested = false;
        card.SelectPasswordRequested += (_, _) => selectRequested = true;

        card.UnlockRequested = (_, _, _) =>
        {
            card.ReportResourceLimit(512L * 1024 * 1024, 100L * 1024 * 1024);
            return Task.FromResult(UnlockOutcome.ResourceLimit);
        };

        UnlockOutcome outcome = await card.SubmitAsync(null, null);

        Assert.Equal(UnlockOutcome.ResourceLimit, outcome);
        Assert.True(card.HasError);
        Assert.Contains(OperationViewModel.FormatBytes(512L * 1024 * 1024), card.Error, StringComparison.Ordinal);
        Assert.Contains(OperationViewModel.FormatBytes(100L * 1024 * 1024), card.Error, StringComparison.Ordinal);
        Assert.Contains("try again", card.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Try again", card.SubmitLabel);
        Assert.True(card.CanSubmit, "the button is the retry");

        // Not a credential failure: no attempt is counted and the password is not selected for retyping.
        Assert.Equal(0, card.FailureCount);
        Assert.False(selectRequested);
    }

    [Fact]
    public async Task The_retry_label_goes_back_to_unlock_after_a_credential_failure()
    {
        var card = new UnlockViewModel(_files, new FakeKdfPreflight(), _log);
        card.Configure(@"C:\vaults\busy.bastion", KdfParameters.Default, null);
        card.HasPassword = true;

        card.UnlockRequested = (_, _, _) =>
        {
            card.ReportResourceLimit(512L * 1024 * 1024, 0);
            return Task.FromResult(UnlockOutcome.ResourceLimit);
        };
        await card.SubmitAsync(null, null);
        Assert.Equal("Try again", card.SubmitLabel);
        Assert.Contains("could not provide it right now", card.Error, StringComparison.Ordinal);

        card.UnlockRequested = (_, _, _) => Task.FromResult(UnlockOutcome.WrongCredentials);
        await card.SubmitAsync(null, null);

        Assert.Equal("Unlock", card.SubmitLabel);
        Assert.Equal(1, card.FailureCount);
    }

    [Fact]
    public void The_new_vault_dialog_marks_the_preset_that_does_not_fit_and_opens_on_the_largest_that_does()
    {
        var dialog = new NewVaultDialogViewModel(_files, _estimator, SmallMachine(), KdfPreset.Strong, _log);

        KdfPresetOption strong = dialog.Presets.Single(p => p.Preset == KdfPreset.Strong);
        KdfPresetOption standard = dialog.Presets.Single(p => p.Preset == KdfPreset.Standard);

        Assert.False(strong.Fits);
        Assert.True(strong.HasFitNote);
        Assert.Contains(OperationViewModel.FormatBytes(OneGiB), strong.FitNote, StringComparison.Ordinal);
        Assert.True(standard.Fits);
        Assert.False(standard.HasFitNote);
        Assert.Same(standard, dialog.SelectedPreset);
    }

    [Fact]
    public void The_new_vault_dialog_honours_the_default_preset_when_it_fits()
    {
        var dialog = new NewVaultDialogViewModel(_files, _estimator, SmallMachine(), KdfPreset.Fast, _log);

        Assert.Equal(KdfPreset.Fast, dialog.SelectedPreset.Preset);
    }

    [Fact]
    public void Choosing_a_preset_that_does_not_fit_blocks_create_and_names_the_one_that_would()
    {
        var dialog = new NewVaultDialogViewModel(_files, _estimator, SmallMachine(), KdfPreset.Standard, _log);
        dialog.Path = @"C:\vaults\new.bastion";
        dialog.Acknowledged = true;
        dialog.ApplyPassword(PasswordStrength.Estimate("a long enough passphrase for the test".AsSpan()), matches: true);
        Assert.True(dialog.CanCreate);

        dialog.SelectedPreset = dialog.Presets.Single(p => p.Preset == KdfPreset.Strong);

        Assert.False(dialog.CanCreate);
        Assert.True(dialog.HasBlockingReason);
        Assert.Contains("Strong", dialog.BlockingReason, StringComparison.Ordinal);
        Assert.Contains("Standard", dialog.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_change_credentials_dialog_gates_apply_on_a_preset_this_machine_can_serve()
    {
        var dialog = new ChangeCredentialsDialogViewModel(
            _files, _estimator, SmallMachine(), KdfParameters.FromPreset(KdfPreset.Strong), 1024, _log);
        dialog.ApplyPassword(PasswordStrength.Estimate("a long enough passphrase for the test".AsSpan()), matches: true, hasCurrent: true);

        // It opens on the vault's current preset even when that one does not fit here.
        Assert.Equal(KdfPreset.Strong, dialog.SelectedPreset.Preset);
        Assert.False(dialog.CanApply);
        Assert.True(dialog.HasMemoryWarning);
        Assert.Contains("Standard", dialog.MemoryWarning, StringComparison.Ordinal);

        dialog.SelectedPreset = dialog.Presets.Single(p => p.Preset == KdfPreset.Standard);

        Assert.True(dialog.CanApply);
        Assert.False(dialog.HasMemoryWarning);
    }

    [Fact]
    public void An_unmeasurable_machine_marks_nothing()
    {
        var dialog = new NewVaultDialogViewModel(_files, _estimator, new FakeKdfPreflight(), KdfPreset.Strong, _log);

        Assert.All(dialog.Presets, p => Assert.True(p.Fits));
        Assert.Equal(KdfPreset.Strong, dialog.SelectedPreset.Preset);
    }
}
