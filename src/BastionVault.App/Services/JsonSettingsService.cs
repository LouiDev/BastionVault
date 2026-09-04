using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace BastionVault.App.Services;

/// <summary>
/// <see cref="AppSettings"/> as JSON under <c>%LOCALAPPDATA%\BastionVault\settings.json</c>. Writes are
/// atomic: the file is written to a sibling temp file, flushed, and then swapped in, so a crash
/// mid-write can never leave a half-written settings file behind.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILog? _log;

    /// <summary>Loads the settings from the default location.</summary>
    /// <param name="log">Optional log for read failures.</param>
    public JsonSettingsService(ILog? log = null)
        : this(AppPaths.SettingsFile, log)
    {
    }

    /// <summary>Loads the settings from a specific file.</summary>
    /// <param name="path">Full path of the settings file.</param>
    /// <param name="log">Optional log for read failures.</param>
    public JsonSettingsService(string path, ILog? log = null)
    {
        _path = path;
        _log = log;
        Current = Load();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public AppSettings Current { get; private set; }

    /// <summary>
    /// Writes the settings through a temporary file. A failed write is swallowed, the way
    /// <see cref="DpapiStore{T}"/> already does: this runs from ordinary UI paths - a column
    /// header click, the preview toggle, the density menu, the list's Unloaded handler - none of
    /// which has a handler, so a read-only or full profile, or an antivirus lock on settings.json,
    /// would otherwise turn a header click into an unhandled exception that reaches the
    /// dispatcher's crash handler, zeroes the vault keys and costs the user their open session.
    /// The in-memory <see cref="Current"/> keeps the change either way.
    /// </summary>
    public void Save()
    {
        try
        {
            WriteFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            _log?.Warn("The settings could not be written; the change is kept in memory only.", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void WriteFile()
    {
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            AppPaths.Ensure(directory);
        }

        string temp = _path + ".tmp";
        string json = JsonSerializer.Serialize(Current, Options);

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {
            File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    /// <summary>Replaces the live settings wholesale (used when a dialog edits a copy).</summary>
    /// <param name="settings">The new values.</param>
    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current.CopyFrom(settings);
        Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_path);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null)
            {
                return new AppSettings();
            }

            // Future schema versions are read best-effort: unknown fields are ignored by the
            // deserialiser and missing ones keep their defaults.
            loaded.SchemaVersion = 1;
            return loaded;
        }
        catch (JsonException ex)
        {
            _log?.Warn("Settings file could not be parsed; defaults are used.", ex);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            _log?.Warn("Settings file could not be read; defaults are used.", ex);
            return new AppSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Warn("Settings file could not be read; defaults are used.", ex);
            return new AppSettings();
        }
    }
}
