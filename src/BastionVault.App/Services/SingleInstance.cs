using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.IO;

namespace BastionVault.App.Services;

/// <summary>
/// One process per vault. A named mutex derived from the vault path is the lock; a named pipe
/// next to it lets the second process ask the first one to come to the front instead of opening
/// the same file twice (two writers of one vault produce a detected conflict, not a merge).
/// </summary>
public sealed class SingleInstance : ISingleInstance
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

        string name = NameFor(path);
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
            mutex.Dispose();
            return null;
        }

        return new VaultLock(mutex, name, _onFocusRequested, _log);
    }

    /// <inheritdoc />
    public void FocusExistingInstance(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var client = new NamedPipeClientStream(".", NameFor(path), PipeDirection.Out);
            client.Connect(500);
            client.Write("focus"u8);
            client.Flush();
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
    }

    private static string NameFor(string path)
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

        byte[] hash = SHA256.HashData(Encoding.Unicode.GetBytes(full.ToUpperInvariant()));
        return Prefix + Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private sealed class VaultLock : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ILog? _log;
        private NamedPipeServerStream? _server;

        public VaultLock(Mutex mutex, string pipeName, Action? onFocusRequested, ILog? log)
        {
            _mutex = mutex;
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

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned any more (abandoned): releasing is best effort.
            }

            _mutex.Dispose();
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
