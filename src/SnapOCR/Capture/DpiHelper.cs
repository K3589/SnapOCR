namespace SnapOCR.Capture;

/// <summary>
/// DIP（96 分之一英寸，XAML 逻辑单位）与物理像素之间的唯一换算入口。
/// 业务代码禁止再出现 <c>* dpi / 96.0</c> 之类的字面量。
/// </summary>
public static class DpiHelper
{
    /// <summary>XAML 的基准 DPI。</summary>
    public const double DefaultDpi = 96.0;

    /// <summary>拿不到 DPI 时的兜底值（100% 缩放）。</summary>
    public const uint FallbackDpi = 96;

    /// <summary>缩放倍率，例如 144 DPI → 1.5。</summary>
    public static double ScaleFactor(uint dpi)
    {
        if (dpi == 0)
        {
            dpi = FallbackDpi;
        }

        return dpi / DefaultDpi;
    }

    /// <summary>DIP → 物理像素（保留小数，用于叠加运算）。</summary>
    public static double DipToPhysical(double dip, uint dpi) => dip * ScaleFactor(dpi);

    /// <summary>物理像素 → DIP。</summary>
    public static double PhysicalToDip(double physical, uint dpi) => physical / ScaleFactor(dpi);

    /// <summary>DIP → 物理像素（取整，用于 BitBlt / 窗口定位）。</summary>
    public static int DipToPhysicalInt(double dip, uint dpi)
        => (int)Math.Round(DipToPhysical(dip, dpi), MidpointRounding.AwayFromZero);

    /// <summary>物理像素 → DIP（取整）。</summary>
    public static int PhysicalToDipInt(double physical, uint dpi)
        => (int)Math.Round(PhysicalToDip(physical, dpi), MidpointRounding.AwayFromZero);

    /// <summary>
    /// 读取某个窗口所在显示器的有效 DPI（需要进程是 Per-Monitor V2 aware，见 app.manifest）。
    /// 失败时返回 <see cref="FallbackDpi"/>。
    /// </summary>
    public static uint GetDpiForWindowSafe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return FallbackDpi;
        }

        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? FallbackDpi : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return FallbackDpi;
        }
        catch (DllNotFoundException)
        {
            return FallbackDpi;
        }
    }

    /// <summary>读取显示器句柄对应的有效 DPI。</summary>
    public static uint GetDpiForMonitorSafe(IntPtr hmonitor)
    {
        if (hmonitor == IntPtr.Zero)
        {
            return FallbackDpi;
        }

        try
        {
            // MDT_EFFECTIVE_DPI = 0
            if (GetDpiForMonitor(hmonitor, 0, out var dpiX, out _) == 0 && dpiX != 0)
            {
                return dpiX;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        return FallbackDpi;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
