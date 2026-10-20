using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ScreenRecorder.Engine.Annotations;

namespace ScreenRecorder.Engine.Capture;

public class WindowsScreenCapturer : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int smIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint pila, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    static WindowsScreenCapturer()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch { }
    }

    public static List<ScreenSourceInfo> EnumerateDisplays()
    {
        var list = new List<ScreenSourceInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(mi);
            if (GetMonitorInfo(hMon, ref mi))
            {
                int width = mi.rcMonitor.right - mi.rcMonitor.left;
                int height = mi.rcMonitor.bottom - mi.rcMonitor.top;
                bool isPrimary = (mi.dwFlags & 1) != 0;

                list.Add(new ScreenSourceInfo
                {
                    Id = $"display:{index}",
                    Name = $"Display {index + 1} ({width}x{height}){(isPrimary ? " - Primary" : "")}",
                    X = mi.rcMonitor.left,
                    Y = mi.rcMonitor.top,
                    Width = width,
                    Height = height,
                    IsPrimary = isPrimary,
                    Type = "display"
                });
                index++;
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            int w = GetSystemMetrics(0);
            int h = GetSystemMetrics(1);
            list.Add(new ScreenSourceInfo
            {
                Id = "display:0",
                Name = $"Primary Display ({w}x{h})",
                X = 0,
                Y = 0,
                Width = w,
                Height = h,
                IsPrimary = true,
                Type = "display"
            });
        }

        return list;
    }

    private readonly int _captureX;
    private readonly int _captureY;
    private readonly int _captureWidth;
    private readonly int _captureHeight;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly bool _captureCursor;
    private readonly AnnotationCompositor _compositor;

    private readonly byte[] _frameBuffer;
    private readonly IntPtr _pBits;
    private readonly IntPtr _hDesktopWnd;
    private readonly IntPtr _hDesktopDC;
    private readonly IntPtr _hMemDC;
    private readonly IntPtr _hDIB;
    private readonly IntPtr _hOldBitmap;

    public WindowsScreenCapturer(int x, int y, int width, int height, int targetWidth, int targetHeight, AnnotationCompositor compositor, bool captureCursor = true)
    {
        _captureX = x;
        _captureY = y;
        _captureWidth = width;
        _captureHeight = height;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _compositor = compositor;
        _captureCursor = captureCursor;

        int bufferSize = _captureWidth * _captureHeight * 4;
        _frameBuffer = new byte[bufferSize];

        _hDesktopWnd = GetDesktopWindow();
        _hDesktopDC = GetWindowDC(_hDesktopWnd);
        _hMemDC = CreateCompatibleDC(_hDesktopDC);

        // Create top-down 32-bit DIBSection so that _pBits points directly to uncompressed BGR0 pixel buffer!
        // Using negative height (-_captureHeight) makes it top-down format instead of bottom-up.
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = _captureWidth;
        bmi.bmiHeader.biHeight = -_captureHeight;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        bmi.bmiHeader.biSizeImage = bufferSize;

        _hDIB = CreateDIBSection(_hMemDC, ref bmi, DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        if (_hDIB == IntPtr.Zero || _pBits == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate DIBSection for screen capture.");
        }

        _hOldBitmap = SelectObject(_hMemDC, _hDIB);
    }

    public byte[] CaptureFrame()
    {
        // 1. BitBlt screen into DIBSection memory DC
        BitBlt(_hMemDC, 0, 0, _captureWidth, _captureHeight, _hDesktopDC, _captureX, _captureY, SRCCOPY);

        // 2. Draw mouse cursor if enabled
        if (_captureCursor)
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(ci);
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0)
            {
                int cursorX = ci.ptScreenPos.x - _captureX;
                int cursorY = ci.ptScreenPos.y - _captureY;
                if (cursorX >= 0 && cursorX < _captureWidth && cursorY >= 0 && cursorY < _captureHeight)
                {
                    DrawIcon(_hMemDC, cursorX, cursorY, ci.hCursor);
                }
            }
        }

        // 3. Composite annotations directly using GDI+ on the memory DC only when active
        if (_compositor.HasAnnotations)
        {
            using var g = Graphics.FromHdc(_hMemDC);
            _compositor.Composite(g, _captureX, _captureY, 1.0f, 1.0f);
        }

        // 4. Copy raw bits directly from the DIBSection memory pointer into _frameBuffer
        Marshal.Copy(_pBits, _frameBuffer, 0, _frameBuffer.Length);

        return _frameBuffer;
    }

    public void Dispose()
    {
        if (_hMemDC != IntPtr.Zero)
        {
            SelectObject(_hMemDC, _hOldBitmap);
            DeleteObject(_hDIB);
            DeleteDC(_hMemDC);
        }

        if (_hDesktopDC != IntPtr.Zero)
        {
            ReleaseDC(_hDesktopWnd, _hDesktopDC);
        }
    }
}
