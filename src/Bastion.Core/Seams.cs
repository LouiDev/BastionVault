namespace Bastion.Core;

/// <summary>
/// The only place Core obtains randomness. Production uses the OS CSPRNG; tests substitute a
/// deterministic sequence to make golden files reproducible.
/// </summary>
public interface IRandomSource
{
    /// <summary>Fills the whole buffer with random bytes.</summary>
    /// <param name="buffer">Destination buffer.</param>
    void Fill(Span<byte> buffer);
}

/// <summary>The only place Core reads the current time.</summary>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The only place Core invents file names and decides where staging lives (FORMAT.md section 8.3 and 8.5).
/// </summary>
public interface IVaultPaths
{
    /// <summary>Name of the temp file a save writes next to the vault: <c>&lt;dir&gt;\&lt;name&gt;.bastion.tmp-&lt;8 hex&gt;</c>.</summary>
    /// <param name="vaultPath">Path of the vault being saved.</param>
    string TempFileFor(string vaultPath);

    /// <summary>Name of the backup that <c>File.Replace</c> creates: <c>&lt;dir&gt;\&lt;name&gt;.bastion.bak-&lt;8 hex&gt;</c>.</summary>
    /// <param name="vaultPath">Path of the vault being saved.</param>
    string BackupFileFor(string vaultPath);

    /// <summary>Name of the staging container: <c>&lt;dir&gt;\&lt;name&gt;.bastion~stage-&lt;guid&gt;</c>.</summary>
    /// <param name="vaultPath">Path of the vault being edited.</param>
    /// <param name="session">Id of the session that owns the container.</param>
    string StagingContainerFor(string vaultPath, Guid session);

    /// <summary>Fallback staging directory (<c>%LOCALAPPDATA%\Bastion\staging</c>) for when the vault directory cannot be used.</summary>
    string FallbackStagingDirectory { get; }

    /// <summary>True when the path sits under a cloud-sync root (OneDrive, Dropbox, iCloudDrive, Google Drive, or a reparse-tagged cloud folder).</summary>
    /// <param name="path">Path to test.</param>
    bool IsUnderCloudSyncRoot(string path);
}
