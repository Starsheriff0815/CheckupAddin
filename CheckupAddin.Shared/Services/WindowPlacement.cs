using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CheckupAddIn.Services
{
    /// <summary>
    /// Remembered window placement (T48, TDD §5.11 / §10.6): monitor + position + size of the
    /// main window and the Logics-Constructor, captured on close and restored before the window
    /// becomes visible. Uses Win32 Get/SetWindowPlacement so restore bounds survive maximizing and
    /// stay correct across monitors with different DPI. Serialized as "left,top,right,bottom,maximized"
    /// (physical pixels, workspace coordinates). Dialogs never use this — they open CenterOwner.
    /// </summary>
    internal static class WindowPlacement
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int   length;
            public int   flags;
            public int   showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT  rcNormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int  cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int  dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const int  SW_HIDE                 = 0;
        private const int  SW_SHOWMAXIMIZED        = 3;
        private const int  MONITOR_DEFAULTTONULL    = 0;
        private const int  MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOSIZE              = 0x0001;
        private const uint SWP_NOZORDER            = 0x0004;
        private const uint SWP_NOACTIVATE          = 0x0010;

        // Title-bar probe: a placement is usable when the left or right end of the title strip
        // lies on a connected monitor (D6) — the user can always grab and move the window.
        private const int TitleProbeInsetX = 40;
        private const int TitleProbeY      = 12;
        private const int MinSidePx        = 100;

        /// <summary>Current placement as "l,t,r,b,max"; null when the window has no HWND.
        /// <paramref name="allowMaximized"/> = false stores the restore bounds as normal state.</summary>
        public static string Capture(Window window, bool allowMaximized)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return null;
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                if (!GetWindowPlacement(hwnd, ref wp)) return null;
                var r = wp.rcNormalPosition;
                bool max = allowMaximized && window.WindowState == WindowState.Maximized;
                return string.Join(",", r.Left.ToString(CultureInfo.InvariantCulture),
                                        r.Top.ToString(CultureInfo.InvariantCulture),
                                        r.Right.ToString(CultureInfo.InvariantCulture),
                                        r.Bottom.ToString(CultureInfo.InvariantCulture),
                                        max ? "1" : "0");
            }
            catch { return null; }
        }

        /// <summary>True when <paramref name="data"/> parses and its title bar is on a connected monitor.
        /// Safe to call before the window has an HWND (decides WindowStartupLocation).</summary>
        public static bool IsUsable(string data) => TryParse(data, out var r, out _) && TitleOnMonitor(r);

        /// <summary>Applies a stored placement. Call from OnSourceInitialized (HWND exists, not yet visible).
        /// Returns false (window untouched) for missing/invalid/off-screen data.</summary>
        public static bool Apply(Window window, string data, bool allowMaximized)
        {
            try
            {
                if (!TryParse(data, out var r, out bool max) || !TitleOnMonitor(r)) return false;
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;
                // SW_HIDE: set the restore bounds without showing the window early; WPF's Show/ShowDialog
                // displays it afterwards. Maximizing is left to WPF's WindowState (maximizes on the
                // monitor that now holds the restore bounds).
                var wp = new WINDOWPLACEMENT
                {
                    length           = Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
                    showCmd          = SW_HIDE,
                    rcNormalPosition = r,
                };
                if (!SetWindowPlacement(hwnd, ref wp)) return false;
                if (allowMaximized && max) window.WindowState = WindowState.Maximized;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Centers the window on the work area of the monitor that holds <paramref name="anchorHwnd"/>
        /// (Inventor's main frame). Runs after pending layout so a just-changed Width/Height is already
        /// on the HWND; a second pass corrects the size change when the move crosses a DPI boundary.</summary>
        public static void CenterOnMonitorOf(Window window, IntPtr anchorHwnd)
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                CenterNow(window, anchorHwnd);
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => CenterNow(window, anchorHwnd)));
            }));
        }

        private static void CenterNow(Window window, IntPtr anchorHwnd)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var mon = MonitorFromWindow(anchorHwnd != IntPtr.Zero ? anchorHwnd : hwnd, MONITOR_DEFAULTTONEAREST);
                var mi  = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi) || !GetWindowRect(hwnd, out var wr)) return;

                int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
                var wa = mi.rcWork;
                int x = wa.Left + Math.Max(0, (wa.Right - wa.Left - w) / 2);
                int y = wa.Top  + Math.Max(0, (wa.Bottom - wa.Top - h) / 2);
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }

        // Format check only (no monitor test) — internal for unit tests.
        internal static bool IsWellFormed(string data) => TryParse(data, out _, out _);

        internal static bool IsMaximized(string data) => TryParse(data, out _, out bool max) && max;

        private static bool TryParse(string data, out RECT r, out bool max)
        {
            r = default; max = false;
            if (string.IsNullOrWhiteSpace(data)) return false;
            var p = data.Split(',');
            if (p.Length != 5) return false;
            var v = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
            r = new RECT { Left = v[0], Top = v[1], Right = v[2], Bottom = v[3] };
            max = p[4].Trim() == "1";
            return r.Right - r.Left >= MinSidePx && r.Bottom - r.Top >= MinSidePx;
        }

        private static bool TitleOnMonitor(RECT r)
        {
            try
            {
                int y = r.Top + TitleProbeY;
                return MonitorFromPoint(new POINT { X = r.Left  + TitleProbeInsetX, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero
                    || MonitorFromPoint(new POINT { X = r.Right - TitleProbeInsetX, Y = y }, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
            }
            catch { return false; }
        }
    }
}
