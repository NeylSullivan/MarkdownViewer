using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace MarkdownViewer.UI;

internal sealed class NativeTitleBar : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _content;
    private readonly FrameworkElement _header;
    private readonly Button _maximizeButton;
    private readonly WindowChrome _chrome;
    private HwndSource? _source;
    private bool _updatePending;
    private bool _disposed;
    private bool _maximizePressed;
    private bool _trackingMouseLeave;

    public NativeTitleBar(Window window, FrameworkElement content, FrameworkElement header, Button maximizeButton)
    {
        _window = window;
        _content = content;
        _header = header;
        _maximizeButton = maximizeButton;
        _chrome = new WindowChrome
        {
            CaptionHeight = 36,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(window, _chrome);
        window.SourceInitialized += SourceInitialized;
        window.StateChanged += GeometryChanged;
        window.Deactivated += Deactivated;
        window.SizeChanged += SizeChanged;
        window.IsVisibleChanged += VisibilityChanged;
        header.SizeChanged += SizeChanged;
    }

    private void SourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WindowMessage);
        QueueUpdate();
    }

    private void GeometryChanged(object? sender, EventArgs e)
    {
        FinishMaximizePress(false);
        QueueUpdate();
    }

    private void Deactivated(object? sender, EventArgs e) => FinishMaximizePress(false);
    private void SizeChanged(object sender, SizeChangedEventArgs e) => QueueUpdate();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueUpdate();

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0084: // WM_NCHITTEST: Windows uses HTMAXBUTTON to offer Snap Layouts.
                if (IsOverMaximize(ScreenPoint(lParam)))
                {
                    handled = true;
                    return new IntPtr(9);
                }
                break;
            case 0x00A0: // WM_NCMOUSEMOVE
                var over = IsOverMaximize(ScreenPoint(lParam));
                _maximizeButton.Tag = over ? (_maximizePressed ? "Pressed" : "Hot") : null;
                if (over && !_trackingMouseLeave)
                {
                    var tracking = new MouseTracking
                    {
                        Size = Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = hwnd
                    };
                    _trackingMouseLeave = TrackMouseEvent(ref tracking);
                }
                // Leave non-client hover messages to Windows so the Snap menu can appear.
                break;
            case 0x02A2: // WM_NCMOUSELEAVE
                _trackingMouseLeave = false;
                if (!_maximizePressed) _maximizeButton.Tag = null;
                break;
            case 0x00A1: // WM_NCLBUTTONDOWN
            case 0x00A3: // WM_NCLBUTTONDBLCLK
                if (IsOverMaximize(ScreenPoint(lParam)))
                {
                    _maximizePressed = true;
                    _maximizeButton.Tag = "Pressed";
                    SetCapture(hwnd);
                    handled = true;
                }
                break;
            case 0x0200 when _maximizePressed: // WM_MOUSEMOVE during capture uses client coordinates.
                _maximizeButton.Tag = IsOverMaximize(ClientPointToScreen(hwnd, lParam)) ? "Pressed" : null;
                handled = true;
                break;
            case 0x0202 when _maximizePressed: // WM_LBUTTONUP
                FinishMaximizePress(IsOverMaximize(ClientPointToScreen(hwnd, lParam)));
                handled = true;
                break;
            case 0x00A2 when _maximizePressed: // WM_NCLBUTTONUP
                FinishMaximizePress(IsOverMaximize(ScreenPoint(lParam)));
                handled = true;
                break;
            case 0x0215: // WM_CAPTURECHANGED
                _maximizePressed = false;
                _maximizeButton.Tag = null;
                break;
            case 0x001F: // WM_CANCELMODE
                FinishMaximizePress(false);
                break;
            case 0x02E0: // DPI, settings, theme, composition
            case 0x001A:
            case 0x031A:
            case 0x031E:
                QueueUpdate();
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximize(Point screenPoint)
    {
        if (!_maximizeButton.IsVisible || !_maximizeButton.IsEnabled) return false;
        var point = _maximizeButton.PointFromScreen(screenPoint);
        return new Rect(_maximizeButton.RenderSize).Contains(point);
    }

    private void FinishMaximizePress(bool activate)
    {
        var pressed = _maximizePressed;
        // ReleaseCapture can synchronously send WM_CAPTURECHANGED.
        _maximizePressed = false;
        _maximizeButton.Tag = null;
        if (pressed && _source is not null && GetCapture() == _source.Handle) ReleaseCapture();
        if (pressed && activate) _maximizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static Point ScreenPoint(IntPtr parameter) => new(
        unchecked((short)parameter.ToInt64()), unchecked((short)(parameter.ToInt64() >> 16)));

    private static Point ClientPointToScreen(IntPtr hwnd, IntPtr parameter)
    {
        var point = ScreenPoint(parameter);
        var nativePoint = new NativePoint { X = (int)point.X, Y = (int)point.Y };
        ClientToScreen(hwnd, ref nativePoint);
        return new Point(nativePoint.X, nativePoint.Y);
    }

    private void QueueUpdate()
    {
        if (_disposed || _updatePending) return;
        _updatePending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _updatePending = false;
            if (!_disposed) UpdateGeometry();
        });
    }

    private void UpdateGeometry()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(_header) is null
            || _window.WindowState == WindowState.Minimized || !GetWindowRect(handle, out var windowRect)) return;
        _chrome.ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness;
        var dpi = VisualTreeHelper.GetDpi(_window);
        var inset = new Thickness();
        if (_window.WindowState == WindowState.Maximized)
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
            {
                inset = new Thickness(
                    Math.Max(0, monitor.Work.Left - windowRect.Left) / dpi.DpiScaleX,
                    Math.Max(0, monitor.Work.Top - windowRect.Top) / dpi.DpiScaleY,
                    Math.Max(0, windowRect.Right - monitor.Work.Right) / dpi.DpiScaleX,
                    Math.Max(0, windowRect.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
            }
        }
        if (_content.Margin != inset)
        {
            _content.Margin = inset;
            QueueUpdate();
            return;
        }

        if (_header.ActualHeight <= 0) return;
        var bottom = _header.PointToScreen(new Point(0, _header.ActualHeight));
        var topHeight = (bottom.Y - windowRect.Top) / dpi.DpiScaleY;
        _chrome.CaptionHeight = Math.Max(0, topHeight - _chrome.ResizeBorderThickness.Top);
    }

    public void Dispose()
    {
        if (_disposed) return;
        FinishMaximizePress(false);
        _disposed = true;
        _window.SourceInitialized -= SourceInitialized;
        _window.StateChanged -= GeometryChanged;
        _window.Deactivated -= Deactivated;
        _window.SizeChanged -= SizeChanged;
        _window.IsVisibleChanged -= VisibilityChanged;
        _header.SizeChanged -= SizeChanged;
        _source?.RemoveHook(WindowMessage);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking { public int Size; public uint Flags; public IntPtr Window; public uint HoverTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref MouseTracking tracking);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
