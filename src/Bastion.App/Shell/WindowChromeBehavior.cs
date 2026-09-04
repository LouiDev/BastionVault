using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Bastion.App.Shell;

/// <summary>
/// Everything the custom title bar needs from Win32 (UI-CONTRACT.md section 1.4).
/// <see cref="AllowsTransparency"/> is never used: the window is a normal layered-free window with
/// a <c>WindowChrome</c>, so DWM still draws the rounded corners, the shadow and the border, and
/// the app still gets Snap Layouts, Aero Snap and correct maximise behaviour on every monitor.
/// </summary>
/// <remarks>
/// Four things have to be done by hand for a custom caption:
/// the DWM attributes (rounded corners, dark mode, border colour);
/// the maximised margin, because a maximised <c>WindowChrome</c> window overhangs the work area by
/// the resize frame; <c>WM_NCHITTEST</c> returning <c>HTMAXBUTTON</c> so Windows 11 shows Snap
/// Layouts over our own maximise button; and <c>WM_GETMINMAXINFO</c> so maximising respects the
/// work area rather than the whole monitor.
/// </remarks>
public sealed class WindowChromeBehavior : IDisposable
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_DPICHANGED = 0x02E0;

    private const int HTCLIENT = 1;
    private const int HTMAXBUTTON = 9;

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private const int DWMWCP_ROUND = 2;

    /// <summary>The Lamplight window border, as DWM wants it: 0x00BBGGRR over #2A3241.</summary>
    private const int BorderColour = 0x0041322A;

    private readonly Window _window;
    private readonly FrameworkElement _contentRoot;
    private readonly FrameworkElement _maximizeButton;

    private HwndSource? _source;
    private bool _maximizeHover;
    private bool _maximizePressed;
    private bool _disposed;

    private WindowChromeBehavior(Window window, FrameworkElement contentRoot, FrameworkElement maximizeButton)
    {
        _window = window;
        _contentRoot = contentRoot;
        _maximizeButton = maximizeButton;
    }

    /// <summary>Attaches the behaviour to a window; call it from the window's constructor.</summary>
    /// <param name="window">The shell window.</param>
    /// <param name="contentRoot">Element that carries the maximised margin.</param>
    /// <param name="maximizeButton">The maximise caption button, for the Snap Layouts hit test.</param>
    public static WindowChromeBehavior Attach(Window window, FrameworkElement contentRoot, FrameworkElement maximizeButton)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(contentRoot);
        ArgumentNullException.ThrowIfNull(maximizeButton);

        var behavior = new WindowChromeBehavior(window, contentRoot, maximizeButton);
        window.SourceInitialized += behavior.OnSourceInitialized;
        window.StateChanged += behavior.OnStateChanged;
        window.DpiChanged += behavior.OnDpiChanged;
        return behavior;
    }

    /// <summary>Detaches every hook.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.StateChanged -= OnStateChanged;
        _window.DpiChanged -= OnDpiChanged;
        _source?.RemoveHook(OnMessage);
        _source = null;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(_window);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(OnMessage);

        ApplyDwmAttributes(helper.Handle);
        ApplyMaximizeMargin();
    }

    private void OnStateChanged(object? sender, EventArgs e) => ApplyMaximizeMargin();

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e) => ApplyMaximizeMargin();

    private static void ApplyDwmAttributes(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = 1;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

        int border = BorderColour;
        DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private void ApplyMaximizeMargin()
    {
        if (_window.WindowState != WindowState.Maximized)
        {
            _contentRoot.Margin = default;
            return;
        }

        var helper = new WindowInteropHelper(_window);
        IntPtr handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;
        double horizontal = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;
        double vertical = (GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;

        _contentRoot.Margin = new Thickness(horizontal, vertical, horizontal, vertical);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaximizeButton(lParam))
                {
                    SetMaximizeHover(true);
                    handled = true;
                    return HTMAXBUTTON;
                }

                SetMaximizeHover(false);
                return IntPtr.Zero;

            case WM_NCMOUSEMOVE:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    SetMaximizeHover(true);
                    handled = true;
                }

                return IntPtr.Zero;

            case WM_NCMOUSELEAVE:
                SetMaximizeHover(false);
                SetMaximizePressed(false);
                return IntPtr.Zero;

            case WM_NCLBUTTONDOWN:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    SetMaximizePressed(true);
                    handled = true;
                }

                return IntPtr.Zero;

            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    SetMaximizePressed(false);
                    _window.WindowState = _window.WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    handled = true;
                }

                return IntPtr.Zero;

            case WM_GETMINMAXINFO:
                ClampToWorkArea(hwnd, lParam);
                return IntPtr.Zero;

            case WM_DPICHANGED:
                ApplyMaximizeMargin();
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!_maximizeButton.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            return false;
        }

        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));

        Point client;
        try
        {
            client = _maximizeButton.PointFromScreen(new Point(x, y));
        }
        catch (InvalidOperationException)
        {
            // The visual is not connected yet; there is nothing to hit.
            return false;
        }

        var bounds = new Rect(0, 0, _maximizeButton.ActualWidth, _maximizeButton.ActualHeight);
        return bounds.Contains(client);
    }

    private void SetMaximizeHover(bool value)
    {
        if (_maximizeHover == value)
        {
            return;
        }

        _maximizeHover = value;
        UpdateMaximizeVisual();
    }

    private void SetMaximizePressed(bool value)
    {
        if (_maximizePressed == value)
        {
            return;
        }

        _maximizePressed = value;
        UpdateMaximizeVisual();
    }

    private void UpdateMaximizeVisual() =>
        _maximizeButton.Tag = _maximizePressed ? "Pressed" : _maximizeHover ? "Hover" : null;

    private static void ClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        MINMAXINFO minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMax.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        minMax.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        minMax.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        minMax.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(minMax, lParam, fDeleteOld: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
