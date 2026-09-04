namespace BastionVault.Core;

/// <summary>Production naming and placement of temp, backup and staging files (FORMAT.md section 8.3 and 8.5).</summary>
public sealed class DefaultVaultPaths : IVaultPaths
{
    /// <summary>Folder names that mark a cloud-sync root when they appear in a path.</summary>
    private static readonly string[] CloudFolderNames =
    [
        "OneDrive",
        "Dropbox",
        "iCloudDrive",
        "Google Drive",
        "GoogleDrive",
        "My Drive",
    ];

    /// <summary>Environment variables the sync clients set to their root.</summary>
    private static readonly string[] CloudRootVariables =
    [
        "OneDrive",
        "OneDriveCommercial",
        "OneDriveConsumer",
    ];

    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c>; the BCL enum does not name it.</summary>
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary><c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>; the BCL enum does not name it.</summary>
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Attributes that mark a placeholder managed by a cloud filter driver.</summary>
    private const FileAttributes CloudPlaceholder = RecallOnOpen | RecallOnDataAccess;

    private readonly IRandomSource _random;
    private readonly string? _fallbackStagingDirectory;

    /// <summary>Creates the default path policy.</summary>
    /// <param name="random">Source of the random suffixes used in temp and backup names.</param>
    /// <param name="fallbackStagingDirectory">Overrides <c>%LOCALAPPDATA%\BastionVault\staging</c> when set.</param>
    public DefaultVaultPaths(IRandomSource random, string? fallbackStagingDirectory = null)
    {
        _random = random;
        _fallbackStagingDirectory = fallbackStagingDirectory;
    }

    /// <inheritdoc />
    public string TempFileFor(string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        return $"{vaultPath}.tmp-{Suffix()}";
    }

    /// <inheritdoc />
    public string BackupFileFor(string vaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        return $"{vaultPath}.bak-{Suffix()}";
    }

    /// <inheritdoc />
    public string StagingContainerFor(string vaultPath, Guid session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        return $"{vaultPath}~stage-{session:D}";
    }

    /// <inheritdoc />
    public string FallbackStagingDirectory =>
        _fallbackStagingDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BastionVault",
            "staging");

    /// <inheritdoc />
    public bool IsUnderCloudSyncRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (string variable in CloudRootVariables)
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root) && IsUnder(full, root))
            {
                return true;
            }
        }

        foreach (string segment in full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            foreach (string name in CloudFolderNames)
            {
                // OneDrive personal and business folders carry a suffix, for example "OneDrive - Contoso".
                if (segment.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    segment.StartsWith(name + " - ", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return HasCloudPlaceholderAttributes(full);
    }

    /// <summary>Eight lower-case hex characters from the randomness seam.</summary>
    private string Suffix()
    {
        Span<byte> bytes = stackalloc byte[4];
        _random.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>True when a path sits inside a directory.</summary>
    /// <param name="path">Fully qualified path.</param>
    /// <param name="directory">Candidate ancestor.</param>
    private static bool IsUnder(string path, string directory)
    {
        try
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when the path or one of its ancestors is a cloud placeholder directory.</summary>
    /// <param name="path">Fully qualified path.</param>
    private static bool HasCloudPlaceholderAttributes(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    continue;
                }

                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 && (attributes & CloudPlaceholder) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
