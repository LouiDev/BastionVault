namespace BastionVault.Core.Tests.Vault;

/// <summary>A throwaway directory that deletes itself, however badly the test that used it ended.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates an empty directory under the system temp folder.</summary>
    /// <param name="label">A short label that appears in the directory name.</param>
    public TempDirectory(string label = "vault")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BastionVaultTests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string Path { get; }

    /// <summary>Combines a name onto the directory.</summary>
    /// <param name="name">Relative name.</param>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Creates a subdirectory and returns its path.</summary>
    /// <param name="name">Relative name.</param>
    public string SubDirectory(string name)
    {
        string path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = System.IO.File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    System.IO.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must never fail a test run.
        }
    }
}
