using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace Bastion.App.Services;

/// <summary>
/// A small DPAPI-protected JSON blob on disk, scoped to the current user. Used for the two lists
/// that would otherwise leak which vaults exist and how often they are saved: the recent-vault
/// list and the rollback record (THREAT-MODEL.md A5).
/// </summary>
/// <typeparam name="T">Shape of the stored document; must have a parameterless constructor.</typeparam>
public sealed class DpapiStore<T>
    where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Bastion.App/v1");

    private readonly string _path;
    private readonly ILog? _log;

    /// <summary>Creates a store over one file.</summary>
    /// <param name="path">Full path of the protected file.</param>
    /// <param name="log">Optional log for read and write failures.</param>
    public DpapiStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log;
    }

    /// <summary>Reads the document, or a fresh one when the file is missing or unreadable.</summary>
    public T Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new T();
            }

            byte[] protectedBytes = File.ReadAllBytes(_path);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<T>(plain, Options) ?? new T();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException ex)
        {
            _log?.Warn("A protected store could not be decrypted; it is being reset.", ex);
            return new T();
        }
        catch (JsonException ex)
        {
            _log?.Warn("A protected store could not be parsed; it is being reset.", ex);
            return new T();
        }
        catch (IOException ex)
        {
            _log?.Warn("A protected store could not be read.", ex);
            return new T();
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Warn("A protected store could not be read.", ex);
            return new T();
        }
    }

    /// <summary>Writes the document atomically, protected for the current user.</summary>
    /// <param name="value">Document to store.</param>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                AppPaths.Ensure(directory);
            }

            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }

            string temp = _path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (CryptographicException ex)
        {
            _log?.Warn("A protected store could not be encrypted; it was not written.", ex);
        }
        catch (IOException ex)
        {
            _log?.Warn("A protected store could not be written.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Warn("A protected store could not be written.", ex);
        }
    }

    /// <summary>Deletes the file, if it exists.</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException ex)
        {
            _log?.Warn("A protected store could not be deleted.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log?.Warn("A protected store could not be deleted.", ex);
        }
    }
}
