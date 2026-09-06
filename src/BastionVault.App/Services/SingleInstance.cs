using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace BastionVault.App.Services;

/// <summary>
/// One process per vault. Named mutexes are the lock; a named pipe next to them lets the second process
/// ask the first one to come to the front instead of opening the same file twice (two writers of one
/// vault produce a detected conflict, not a merge).
/// </summary>
/// <remarks>
/// Identity (UI-CONTRACT.md section 5): a vault that exists is identified by its <em>file id</em>, the
/// volume serial number plus the 128-bit file id from <c>GetFileInformationByHandleEx</c>, so the same
/// file reached through a junction, a mapped drive letter or a UNC path is one vault. The normalised,
/// upper-cased path is taken <em>as well</em>, because a vault that is about to be created has no file id
/// yet and is guarded by its path alone until it exists; a later opener therefore checks both names.
/// After a create the shell re-acquires the lock so the new file is guarded by its id too. A file whose id
/// cannot be read (locked exclusively by another program, an exotic file system) falls back to the path.
/// </remarks>
public sealed partial class SingleInstance : ISingleInstance
{
    private const string Prefix = "BastionVault.Vault.";

    private readonly ILog? _log;
    private readonly Action? _onFocusRequested;

    /// <summary>Creates the service.</summary>
    /// <param name="onFocusRequested">Called on a background thread when another process asks this one to come forward.</param>
    /// <param name="log">Optional log.</param>
    public SingleInstance(Action? onFocusRequested = null, ILog? log = null)
    {
        _onFocusRequested = onFocusRequested;
        _log = log;
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireVault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IReadOnlyList<string> names = NamesFor(path, _log);
        var held = new List<Mutex>(names.Count);

        foreach (string name in names)
        {
            var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out _);

            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing; the lock is ours.
                owned = true;
            }

            if (!owned)
            {
                // Another process holds this vault under one of its names; give back what was taken.
                mutex.Dispose();
                foreach (Mutex taken in held)
                {
                    Release(taken);
                }

                return null;
            }

            held.Add(mutex);
        }

        // The pipe listens on the primary name (the file id when there is one); a caller that was
        // refused tries every name, so it reaches this server whichever spelling it used.
        return new VaultLock(held, names[0], _onFocusRequested, _log);
    }

    /// <inheritdoc />
    public void FocusExistingInstance(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (string name in NamesFor(path, _log))
        {
            if (TryFocus(name))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The mutex and pipe names a path is guarded under, primary first: the file-id name when the file
    /// exists and its id can be read, then always the path name.
    /// </summary>
    /// <param name="path">Vault path as the user gave it.</param>
    /// <param name="log">Optional log for an id that could not be read.</param>
    internal static IReadOnlyList<string> NamesFor(string path, ILog? log = null)
    {
        var names = new List<string>(2);

        if (FileIdentity(path, log) is { } identity)
        {
            names.Add(Prefix + Hash("id:" + identity));
        }

        names.Add(Prefix + Hash("path:" + NormalisedPath(path)));
        return names;
    }

    /// <summary>
    /// The file's identity on its volume, "volume serial:128-bit file id" in hex, or <see langword="null"/>
    /// when the file does not exist or its id cannot be read. Opened for reading with every share flag, so
    /// a session that already holds the file (read, share read+delete) is not disturbed and does not block.
    /// </summary>
    /// <param name="path">Vault path.</param>
    /// <param name="log">Optional log.</param>
    internal static string? FileIdentity(string path, ILog? log = null)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var info = default(FileIdInfo);
            if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, ref info, (uint)Marshal.SizeOf<FileIdInfo>()))
            {
                log?.Warn($"The vault's file id could not be read (Win32 error {Marshal.GetLastPInvokeError()}); the path guards it instead.");
                return null;
            }

            return string.Create(null, stackalloc char[64], $"{info.VolumeSerialNumber:X16}:{info.FileIdHigh:X16}{info.FileIdLow:X16}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.Warn("The vault's file id could not be read; the path guards it instead.", ex);
            return null;
        }
    }

    private static string NormalisedPath(string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            full = path;
        }

        return full.ToUpperInvariant();
    }

    private static string Hash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(text));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static void Release(Mutex mutex)
    {
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned any more (abandoned): releasing is best effort.
        }

        mutex.Dispose();
    }

    private bool TryFocus(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(500);
            client.Write("focus"u8);
            client.Flush();
            return true;
        }
        catch (TimeoutException ex)
        {
            _log?.Warn("The process holding this vault did not answer.", ex);
        }
        catch (IOException ex)
        {
            _log?.Warn("The process holding this vault could not be reached.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The pipe name is machine-global while the mutex is session-local, so the pipe on
            // that name is not necessarily ours.
            _log?.Warn("The single-instance pipe could not be opened.", ex);
        }

        return false;
    }

    /// <summary><c>FileIdInfo</c> of <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
    private const int FileIdInfoClass = 18;

    /// <summary><c>FILE_ID_INFO</c>: volume serial number and the 128-bit file id (ReFS-safe).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        /// <summary>Serial number of the volume the file lives on.</summary>
        public ulong VolumeSerialNumber;

        /// <summary>Low 64 bits of the file id.</summary>
        public ulong FileIdLow;

        /// <summary>High 64 bits of the file id.</summary>
        public ulong FileIdHigh;
    }

    /// <summary>Reads one class of file information for an open handle.</summary>
    /// <param name="handle">The file handle.</param>
    /// <param name="infoClass">Which structure to fill.</param>
    /// <param name="info">The structure.</param>
    /// <param name="size">Size of the structure.</param>
    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, ref FileIdInfo info, uint size);

    private sealed class VaultLock : IDisposable
    {
        private readonly IReadOnlyList<Mutex> _mutexes;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ILog? _log;
        private NamedPipeServerStream? _server;

        public VaultLock(IReadOnlyList<Mutex> mutexes, string pipeName, Action? onFocusRequested, ILog? log)
        {
            _mutexes = mutexes;
            _log = log;

            if (onFocusRequested is not null)
            {
                _ = ListenAsync(pipeName, onFocusRequested, _cancellation.Token);
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try
            {
                _server?.Dispose();
            }
            catch (IOException)
            {
                // The pipe may already be torn down; nothing to recover.
            }

            foreach (Mutex mutex in _mutexes)
            {
                Release(mutex);
            }

            _cancellation.Dispose();
        }

        /// <summary>Consecutive failures after which the listener gives up for good.</summary>
        private const int MaxConsecutiveFailures = 8;

        /// <summary>
        /// Serves the come-to-front handshake. The pipe name is machine-global while the mutex
        /// that guards the vault is session-local, so another local process can hold this name
        /// and every attempt to create the server fails: the loop therefore backs off between
        /// attempts, logs once rather than once per iteration (the log file rolls at 1 MiB and
        /// stops rolling after 99 files), catches everything - the task is discarded, so an
        /// escaping exception would silently kill the handshake - and eventually gives up.
        /// </summary>
        private async Task ListenAsync(string pipeName, Action onFocusRequested, CancellationToken ct)
        {
            int failures = 0;
            bool reported = false;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _server = CreateServer(pipeName);

                    await _server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    byte[] buffer = new byte[16];
                    int read = await _server.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read > 0)
                    {
                        onFocusRequested();
                    }

                    failures = 0;
                    reported = false;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (!reported)
                    {
                        _log?.Warn("The single-instance pipe failed; retrying with a backoff.", ex);
                        reported = true;
                    }

                    if (failures >= MaxConsecutiveFailures)
                    {
                        _log?.Warn("The single-instance pipe kept failing; the come-to-front handshake is off for this vault.");
                        return;
                    }

                    try
                    {
                        await Task.Delay(BackoffFor(failures), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    NamedPipeServerStream? server = _server;
                    _server = null;
                    server?.Dispose();
                }
            }
        }

        private static TimeSpan BackoffFor(int failures) =>
            TimeSpan.FromMilliseconds(Math.Min(5000, 100 * Math.Pow(2, failures - 1)));

        /// <summary>
        /// Creates the server with an ACL that admits only the current user, so the handshake
        /// cannot be driven by another account on a shared machine. A platform that will not take
        /// the ACL falls back to the default one rather than losing the handshake.
        /// </summary>
        private static NamedPipeServerStream CreateServer(string pipeName)
        {
            try
            {
                using WindowsIdentity me = WindowsIdentity.GetCurrent();
                if (me.User is { } user)
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

                    return NamedPipeServerStreamAcl.Create(
                        pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 0,
                        outBufferSize: 0,
                        security);
                }
            }
            catch (PlatformNotSupportedException)
            {
                // Fall through to the default ACL.
            }

            return new NamedPipeServerStream(
                pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
    }
}
