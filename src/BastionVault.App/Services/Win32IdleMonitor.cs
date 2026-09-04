using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace BastionVault.App.Services;

/// <summary>
/// System-wide idle time from <c>GetLastInputInfo</c>, polled every five seconds. System-wide is
/// the point: a vault must lock while the user is reading e-mail in another app, not only when
/// Bastion Vault itself is untouched.
/// </summary>
public sealed class Win32IdleMonitor : IIdleMonitor, IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _fired;

    /// <summary>Starts the monitor; polling stays off until <see cref="Enabled"/> is set.</summary>
    public Win32IdleMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _timer.Tick += OnTick;
    }

    /// <inheritdoc />
    public event EventHandler? IdleThresholdReached;

    /// <inheritdoc />
    public TimeSpan Idle
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info))
            {
                return TimeSpan.Zero;
            }

            uint now = GetTickCount();
            uint idleMilliseconds = unchecked(now - info.dwTime);
            return TimeSpan.FromMilliseconds(idleMilliseconds);
        }
    }

    /// <inheritdoc />
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value == _timer.IsEnabled)
            {
                return;
            }

            _fired = false;
            _timer.IsEnabled = value;
        }
    }

    /// <summary>Stops polling.</summary>
    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    private void OnTick(object? sender, EventArgs e)
    {
        if (Threshold <= TimeSpan.Zero)
        {
            return;
        }

        if (Idle >= Threshold)
        {
            if (!_fired)
            {
                _fired = true;
                IdleThresholdReached?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _fired = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
