using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TNovDesktop
{
    /// <summary>
    /// Рабочая область текущего монитора (экран минус панель задач), в DIP.
    /// Нужна, потому что окно без рамки (WindowStyle=None + AllowsTransparency)
    /// при WindowState.Maximized перекрывает панель задач.
    /// </summary>
    internal static class WindowWorkArea
    {
        private const uint MonitorDefaultToNearest = 2;

        public static Rect Get(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var work = info.rcWork;
                        var source = PresentationSource.FromVisual(window);
                        Matrix toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                        Point topLeft = toDip.Transform(new Point(work.left, work.top));
                        Point bottomRight = toDip.Transform(new Point(work.right, work.bottom));
                        return new Rect(topLeft, bottomRight);
                    }
                }
            }

            return SystemParameters.WorkArea;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
