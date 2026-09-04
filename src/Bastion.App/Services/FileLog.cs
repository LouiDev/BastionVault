using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using Bastion.Core;

namespace Bastion.App.Services;

/// <summary>
/// A tiny rolling text log. One file per day, at most <see cref="MaxFiles"/> kept, rolled when a
/// file passes <see cref="MaxBytes"/>. Callers keep their own messages free of entry names,
/// in-vault paths, keys, salts and ids; exception messages are not under the caller's control -
/// Core interpolates the file path into several of them and .NET's own IOException and
/// UnauthorizedAccessException always embed the full path, and an export path is built from an
/// in-vault path - so every message written here is scrubbed of anything path-shaped first
/// (UI-CONTRACT.md section 1.13, THREAT-MODEL.md A5).
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const long MaxBytes = 1024 * 1024;
    private const int MaxFiles = 7;

    private readonly Lock _gate = new();
    private readonly string _directory;
    private bool _disabled;

    /// <summary>Creates a log in the default directory (<c>%LOCALAPPDATA%\Bastion\logs</c>).</summary>
    public FileLog()
        : this(AppPaths.LogDirectory)
    {
    }

    /// <summary>Creates a log in a specific directory.</summary>
    /// <param name="directory">Directory the log files live in; created when missing.</param>
    public FileLog(string directory)
    {
        _directory = directory;
        try
        {
            AppPaths.Ensure(_directory);
            Trim();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // This runs before the crash handlers are installed, so a throw here would end the
            // process with nothing written anywhere to say why. A log that cannot be opened - a
            // denied directory, a path the framework rejects - goes quiet instead.
            _disabled = true;
        }
    }

    /// <inheritdoc />
    public void Info(string message) => Write("INF", message, null);

    /// <inheritdoc />
    public void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    /// <summary>Flushes nothing (every write is flushed) and stops further writes.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disabled = true;
        }
    }

    private void Write(string level, string message, Exception? ex)
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                string path = CurrentFile();
                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff K", CultureInfo.InvariantCulture))
                    .Append("  ")
                    .Append(level)
                    .Append("  ")
                    .Append(Scrub(message));

                for (Exception? current = ex; current is not null; current = current.InnerException)
                {
                    line.Append("  | ").Append(current.GetType().FullName);

                    if (current is VaultException vault)
                    {
                        line.Append('[').Append(vault.Code).Append(']');
                    }

                    line.Append(": ").Append(Scrub(current.Message));
                }

                line.AppendLine();
                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A log that cannot be written must never take the app down, and this is the one
                // rule with no exceptions to it: the crash handlers call Write on their way out, so
                // a Write that throws turns a reported crash into a silent one. Everything is caught
                // here - a locked file, a denied directory, a regex match timeout inside Scrub, a
                // path the framework will not accept - and the log simply goes quiet.
                _disabled = true;
            }
        }
    }

    /// <summary>
    /// Replaces anything path-shaped with a placeholder. Over-redaction is the right side to err
    /// on: a log line is a diagnostic, and a file name is content.
    /// </summary>
    /// <param name="text">Text to scrub; may be a caller message or an exception message.</param>
    internal static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return PathLike.Replace(text.ReplaceLineEndings(" "), "<path>");
        }
        catch (RegexMatchTimeoutException)
        {
            // Scrubbing is a redaction, not a formatting nicety: if it cannot be done in the budget
            // the text is dropped rather than logged unredacted.
            return "<unscrubbable>";
        }
    }

    /// <summary>
    /// A drive-rooted path, a UNC path, or a separator-rooted in-vault path, up to the quote or
    /// angle bracket that usually closes it in a framework message.
    /// </summary>
    private static readonly Regex PathLike = new(
        @"(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]|(?<!\w)[\\/](?=[^\\/\s]))[^""'<>|\r\n]*",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(200));

    private string CurrentFile()
    {
        string stem = System.IO.Path.Combine(
            _directory,
            "bastion-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        string path = stem + ".log";

        var info = new FileInfo(path);
        if (info.Exists && info.Length >= MaxBytes)
        {
            for (int index = 1; index < 100; index++)
            {
                string rolled = $"{stem}.{index}.log";
                if (!File.Exists(rolled) || new FileInfo(rolled).Length < MaxBytes)
                {
                    return rolled;
                }
            }
        }

        return path;
    }

    private void Trim()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("bastion-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxFiles)
            .ToArray();

        foreach (FileInfo file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // A locked old log is not worth failing start-up for.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
