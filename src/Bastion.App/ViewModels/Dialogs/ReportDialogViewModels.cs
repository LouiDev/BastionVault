using System.Globalization;
using Bastion.App.Input;
using Bastion.App.Services;
using Bastion.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bastion.App.ViewModels.Dialogs;

/// <summary>One row of a report list: a glyph, a path and a reason.</summary>
/// <param name="Glyph">Resource key of the glyph to show.</param>
/// <param name="Path">Path or name the row is about.</param>
/// <param name="Detail">Why the row is in the report.</param>
/// <param name="IsFailure">True when the row is an error rather than a note.</param>
public sealed record ReportRow(string Glyph, string Path, string Detail, bool IsFailure);

/// <summary>
/// The Verify report (UI-CONTRACT.md section 7): files, bytes, elapsed time, throughput and every
/// failure with its path. It stays re-openable from the status bar, so the numbers are phrased to
/// be readable long after the run.
/// </summary>
public sealed class VerifyReportDialogViewModel : DialogViewModelBase<bool>
{
    /// <summary>Creates the dialog over a finished report.</summary>
    /// <param name="report">The report Core produced.</param>
    /// <param name="vaultName">Name of the vault, for the header line.</param>
    public VerifyReportDialogViewModel(VerifyReport report, string vaultName)
    {
        ArgumentNullException.ThrowIfNull(report);

        Report = report;
        VaultName = vaultName;
        Title = report.IsClean ? "Verify: clean" : $"Verify: {report.Failures.Count} failure{(report.Failures.Count == 1 ? string.Empty : "s")}";

        Rows = [.. report.Failures.Select(f => new ReportRow(
            "Glyph.Error",
            f.VaultPath,
            f.ChunkIndex is { } chunk ? $"{f.Detail} (chunk {chunk})" : f.Detail,
            true))];
    }

    /// <summary>The report Core produced.</summary>
    public VerifyReport Report { get; }

    /// <summary>Name of the vault the report is about.</summary>
    public string VaultName { get; }

    /// <summary>Every failure, as rows.</summary>
    public IReadOnlyList<ReportRow> Rows { get; }

    /// <summary>True when nothing failed.</summary>
    public bool IsClean => Report.IsClean;

    /// <summary>"1,204 files - 3.1 GB - 12.4 s - 254 MB/s".</summary>
    public string Summary
    {
        get
        {
            double seconds = Math.Max(Report.Elapsed.TotalSeconds, 0.001);
            double megabytesPerSecond = Report.BytesChecked / seconds / (1024 * 1024);
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{Report.FilesChecked:N0} files · {OperationViewModel.FormatBytes(Report.BytesChecked)} · {Report.Elapsed.TotalSeconds:N1} s · {megabytesPerSecond:N0} MB/s");
        }
    }

    /// <summary>"The layout is intact." or the opposite.</summary>
    public string LayoutLine => Report.LayoutOk
        ? "Every blob tiles the data section exactly."
        : "The data section layout does not match the index. Do not save over this file.";
}

/// <summary>
/// The Import report: what was imported and what was skipped, renamed or unreadable. Import is
/// continue-on-error by design, so this dialog is the only place the user finds out.
/// </summary>
public sealed class ImportReportDialogViewModel : DialogViewModelBase<bool>
{
    /// <summary>Creates the dialog over a finished import.</summary>
    /// <param name="result">The result Core produced.</param>
    public ImportReportDialogViewModel(ImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Import = result;
        Title = result.Issues.Count == 0
            ? "Import finished"
            : $"Import finished with {result.Issues.Count} note{(result.Issues.Count == 1 ? string.Empty : "s")}";

        Rows = [.. result.Issues.Select(i => new ReportRow(
            GlyphFor(i.Kind),
            i.SourcePath,
            i.Detail ?? DescriptionFor(i.Kind),
            i.Kind is ImportIssueKind.Locked or ImportIssueKind.Unreadable or ImportIssueKind.ChangedWhileReading))];
    }

    /// <summary>The result Core produced.</summary>
    public ImportResult Import { get; }

    /// <summary>Every issue, as rows.</summary>
    public IReadOnlyList<ReportRow> Rows { get; }

    /// <summary>True when nothing needed reporting.</summary>
    public bool IsClean => Import.Issues.Count == 0;

    /// <summary>"128 items - 412 MB imported".</summary>
    public string Summary =>
        $"{Import.Imported.Count:N0} items · {OperationViewModel.FormatBytes(Import.BytesImported)} imported";

    private static string GlyphFor(ImportIssueKind kind) => kind switch
    {
        ImportIssueKind.Locked or ImportIssueKind.Unreadable or ImportIssueKind.ChangedWhileReading => "Glyph.Error",
        ImportIssueKind.Cancelled or ImportIssueKind.Skipped => "Glyph.Info",
        _ => "Glyph.Warning",
    };

    private static string DescriptionFor(ImportIssueKind kind) => kind switch
    {
        ImportIssueKind.SkippedReparsePoint => "Skipped: junctions and symlinks are never followed.",
        ImportIssueKind.Locked => "Locked by another program.",
        ImportIssueKind.Unreadable => "Could not be read.",
        ImportIssueKind.Renamed => "Renamed to fit the vault's naming rules.",
        ImportIssueKind.ChangedWhileReading => "Changed while it was being read, so it was dropped.",
        ImportIssueKind.TooDeep => "Deeper than the depth limit.",
        ImportIssueKind.Cancelled => "Not reached before the import was cancelled.",
        ImportIssueKind.Skipped => "Skipped: an entry with that name already exists.",
        _ => "Reported by the importer.",
    };
}

/// <summary>
/// The name-conflict dialog: Replace, Skip or Keep both, with "do this for the rest of this import".
/// </summary>
/// <remarks>
/// The label deliberately does not name a number. How many more conflicts an import will hit cannot be
/// known before it runs - Core asks one conflict at a time - so a count would be a guess dressed up as
/// a fact. <see cref="NameConflictDialogViewModel.Remaining"/> is still the upper bound of items left to
/// process, and it only decides whether the check box is worth showing at all.
/// </remarks>
public sealed partial class NameConflictDialogViewModel : DialogViewModelBase<ConflictDecision>
{
    [ObservableProperty]
    private bool _applyToAll;

    /// <summary>Creates the dialog for one conflict.</summary>
    /// <param name="name">The colliding name.</param>
    /// <param name="destination">Where it collides, as an in-vault path.</param>
    /// <param name="remaining">Upper bound of items still to process, including this one.</param>
    public NameConflictDialogViewModel(string name, string destination, int remaining)
    {
        Name = name;
        Destination = destination;
        Remaining = remaining;
        Title = "Name already used";
    }

    /// <summary>The colliding name.</summary>
    public string Name { get; }

    /// <summary>Where it collides.</summary>
    public string Destination { get; }

    /// <summary>Upper bound of the items still to process, including this one.</summary>
    public int Remaining { get; }

    /// <summary>Label of the "do this for all" check box.</summary>
    public string ApplyToAllLabel => "Do this for the rest of this import";

    /// <summary>True when something is still to come, so the check box is worth showing.</summary>
    public bool CanApplyToAll => Remaining > 1;

    /// <summary>Overwrites the existing entry.</summary>
    [RelayCommand]
    public void Replace() => Close(ApplyToAll ? ConflictDecision.ReplaceAll : ConflictDecision.Replace);

    /// <summary>Leaves the existing entry alone.</summary>
    [RelayCommand]
    public void Skip() => Close(ApplyToAll ? ConflictDecision.SkipAll : ConflictDecision.Skip);

    /// <summary>Keeps both by giving the incoming item a unique name.</summary>
    [RelayCommand]
    public void KeepBoth() => Close(ApplyToAll ? ConflictDecision.RenameAll : ConflictDecision.Rename);

    /// <inheritdoc />
    public override bool Accept()
    {
        KeepBoth();
        return true;
    }
}

/// <summary>The About dialog: version, format version, and the one sentence that matters.</summary>
public sealed class AboutDialogViewModel : DialogViewModelBase<bool>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="version">Assembly version string.</param>
    public AboutDialogViewModel(string version)
    {
        Version = version;
        Title = "About Bastion";
    }

    /// <summary>Assembly version string.</summary>
    public string Version { get; }

    /// <summary>Vault format version this build reads and writes.</summary>
    public string FormatVersion => "Vault format 1";

    /// <summary>The cryptography, in one instrument line.</summary>
    public string CryptoLine => "Argon2id (RFC 9106) · AES-256-GCM · HKDF-SHA256 · BLAKE2b";

    /// <summary>The honest sentence.</summary>
    public string Promise =>
        "Bastion protects the contents of a vault file against someone who obtains the file. It does not protect against someone who controls this machine while the vault is open.";
}

/// <summary>One row of the Shortcuts dialog.</summary>
/// <param name="Keys">The gesture text.</param>
/// <param name="Action">What the gesture does.</param>
public sealed record ShortcutRow(string Keys, string Action);

/// <summary>One section of the Shortcuts dialog.</summary>
/// <param name="Heading">Section heading.</param>
/// <param name="Rows">The rows in the section.</param>
public sealed record ShortcutSection(string Heading, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The Shortcuts dialog. Every row comes from <see cref="KeyMap"/>, which is also what binds the
/// keys, so the dialog cannot drift from the real bindings.
/// </summary>
public sealed class ShortcutsDialogViewModel : DialogViewModelBase<bool>
{
    /// <summary>Creates the dialog from the keymap.</summary>
    public ShortcutsDialogViewModel()
    {
        Title = "Keyboard shortcuts";
        Sections =
        [
            .. KeyMap.Entries
                .GroupBy(e => e.Category)
                .Select(g => new ShortcutSection(
                    HeadingFor(g.Key),
                    [.. g.Select(e => new ShortcutRow(e.Display, e.Description))])),
        ];
    }

    /// <summary>The keymap, grouped into sections.</summary>
    public IReadOnlyList<ShortcutSection> Sections { get; }

    private static string HeadingFor(ShortcutCategory category) => category switch
    {
        ShortcutCategory.Vault => "Vault",
        ShortcutCategory.Content => "Content",
        ShortcutCategory.Editing => "Editing",
        ShortcutCategory.Navigation => "Navigation",
        _ => "Window",
    };
}
