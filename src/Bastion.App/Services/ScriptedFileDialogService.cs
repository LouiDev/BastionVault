namespace Bastion.App.Services;

/// <summary>
/// A test-only stand-in for the OS pickers. A UI-automation harness cannot drive the common file
/// dialogs reliably (they are separate windows owned by the shell, and their automation tree
/// differs between Windows builds), so the integration run starts the app with
/// <c>--test-pick-*</c> arguments and every picker answers from those instead of opening a window.
/// Anything that was not scripted falls through to the real <see cref="FileDialogService"/>, so a
/// mis-typed flag shows an OS dialog rather than silently cancelling.
/// </summary>
/// <remarks>
/// This is wired only when at least one <c>--test-pick-*</c> argument is present. It writes a line
/// to the log for every answer so the harness can tell a picker was reached.
/// </remarks>
public sealed class ScriptedFileDialogService : IFileDialogService
{
    /// <summary>Prefix of every argument this service understands.</summary>
    public const string ArgumentPrefix = "--test-pick-";

    private readonly IFileDialogService _fallback;
    private readonly ILog _log;
    private readonly IReadOnlyDictionary<string, string> _answers;

    /// <summary>Creates the service.</summary>
    /// <param name="fallback">Used for every picker that was not scripted.</param>
    /// <param name="answers">Picker name (the part after the prefix) to answer.</param>
    /// <param name="log">Log for the answers.</param>
    public ScriptedFileDialogService(IFileDialogService fallback, IReadOnlyDictionary<string, string> answers, ILog log)
    {
        _fallback = fallback;
        _answers = answers;
        _log = log;
    }

    /// <summary>
    /// Reads every <c>--test-pick-&lt;name&gt;=&lt;value&gt;</c> argument.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>Picker name to answer; empty when the app was started normally.</returns>
    public static IReadOnlyDictionary<string, string> ParseAnswers(IEnumerable<string> args)
    {
        Dictionary<string, string> answers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string arg in args)
        {
            if (!arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals <= ArgumentPrefix.Length)
            {
                continue;
            }

            answers[arg[ArgumentPrefix.Length..equals]] = arg[(equals + 1)..].Trim('"');
        }

        return answers;
    }

    /// <inheritdoc />
    public string? PickVaultToOpen() => Answer("vault-open") ?? _fallback.PickVaultToOpen();

    /// <inheritdoc />
    public string? PickVaultToCreate(string suggestedName) =>
        Answer("vault-create") ?? _fallback.PickVaultToCreate(suggestedName);

    /// <inheritdoc />
    public string? PickKeyFile() => Answer("keyfile") ?? _fallback.PickKeyFile();

    /// <inheritdoc />
    public string? PickKeyFileToCreate() => Answer("keyfile-create") ?? _fallback.PickKeyFileToCreate();

    /// <inheritdoc />
    public IReadOnlyList<string> PickFilesToImport() =>
        Answer("import-files") is { } list
            ? list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : _fallback.PickFilesToImport();

    /// <inheritdoc />
    public string? PickFolderToImport() => Answer("import-folder") ?? _fallback.PickFolderToImport();

    /// <inheritdoc />
    public string? PickExportFolder() => Answer("export-folder") ?? _fallback.PickExportFolder();

    private string? Answer(string name)
    {
        if (!_answers.TryGetValue(name, out string? value))
        {
            return null;
        }

        _log.Info($"Scripted picker '{name}' answered.");
        return value;
    }
}
