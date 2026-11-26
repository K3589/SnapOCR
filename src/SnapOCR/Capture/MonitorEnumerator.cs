using SnapOCR.Interop;

namespace SnapOCR.Capture;

/// <summary>一块显示器在虚拟桌面坐标系中的物理像素矩形（可为负坐标）。</summary>
public readonly record struct MonitorBounds(
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi,
    bool IsPrimary,
    string DeviceName)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    /// <summary>与另一个物理矩形的交集；无交集时返回空矩形。</summary>
    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? PixelRect.Empty
            : new PixelRect(left, top, right - left, bottom - top);
    }
}

/// <summary>虚拟桌面坐标系中的物理像素矩形。</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public static PixelRect Empty => new(0, 0, 0, 0);

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>把拖拽的两个角点规范成左上角 + 宽高。</summary>
    public static PixelRect FromCorners(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

/// <summary>枚举当前所有显示器（物理像素 + 每屏 DPI）。</summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorBounds> GetMonitors()
    {
        var results = new List<MonitorBounds>(2);

        // 委托必须在调用期间保持存活，否则 GC 回收会导致回调崩溃。
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                results.Add(new MonitorBounds(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Width,
                    info.rcMonitor.Height,
                    DpiHelper.GetDpiForMonitorSafe(hMonitor),
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    string.IsNullOrWhiteSpace(info.szDevice) ? $"DISPLAY{hMonitor}" : info.szDevice));
            }

            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || results.Count == 0)
        {
            // 枚举失败时退回主屏，保证应用还能用。
            var width = NativeMethods.GetSystemMetrics(0);
            var height = NativeMethods.GetSystemMetrics(1);
            results.Add(new MonitorBounds(0, 0, Math.Max(width, 1), Math.Max(height, 1), DpiHelper.FallbackDpi, true, "PRIMARY"));
        }

        GC.KeepAlive(callback);
        return results;
    }
}
