using System.Runtime.InteropServices;
using SnapOCR.Interop;

namespace SnapOCR.Capture;

/// <summary>一块显示器被 BitBlt 下来的整屏 BGRA 像素（top-down，stride = Width * 4）。</summary>
public sealed class CapturedFrame
{
    public CapturedFrame(MonitorBounds monitor, byte[] pixels)
    {
        Monitor = monitor;
        Pixels = pixels;
    }

    public MonitorBounds Monitor { get; }

    public byte[] Pixels { get; }

    public int Width => Monitor.Width;

    public int Height => Monitor.Height;

    public int Stride => Width * 4;

    public PixelRect Bounds => new(Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height);

    /// <summary>从整屏像素里裁出虚拟桌面坐标下的矩形，坐标会自动转成屏幕内局部坐标并夹取。</summary>
    public CroppedImage Crop(PixelRect screenRect)
    {
        var left = Math.Clamp(screenRect.Left - Monitor.Left, 0, Width);
        var top = Math.Clamp(screenRect.Top - Monitor.Top, 0, Height);
        var right = Math.Clamp(screenRect.Right - Monitor.Left, 0, Width);
        var bottom = Math.Clamp(screenRect.Bottom - Monitor.Top, 0, Height);

        var w = right - left;
        var h = bottom - top;
        if (w <= 0 || h <= 0)
        {
            return CroppedImage.Empty;
        }

        var buffer = new byte[w * h * 4];
        var rowBytes = w * 4;
        for (var y = 0; y < h; y++)
        {
            var sourceOffset = ((top + y) * Stride) + (left * 4);
            Buffer.BlockCopy(Pixels, sourceOffset, buffer, y * rowBytes, rowBytes);
        }

        return new CroppedImage(buffer, w, h);
    }
}

/// <summary>裁剪后的 BGRA 图像（紧排，stride = Width * 4）。</summary>
public readonly record struct CroppedImage(byte[] Bgra, int Width, int Height)
{
    public static CroppedImage Empty => new(Array.Empty<byte>(), 0, 0);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public int Stride => Width * 4;
}

/// <summary>
/// 用 GDI BitBlt 直接读取桌面物理像素。
/// 刻意不使用 Windows.Graphics.Capture（那会弹系统选择器，且需要用户确认）。
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    // 每块屏幕复用一个 DIB surface，避免每次热键都重新分配几十 MB。
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>
    /// 启动时先抓一次，把 GDI 资源、DIB 缓存的首次成本挪走。
    /// 这样第一次按热键也不会因为创建 DIB 而多花几十毫秒。
    /// </summary>
    public void Warmup()
    {
        try
        {
            _ = CaptureAll();
        }
        catch (Exception)
        {
            // 预热失败不影响正式使用。
        }
    }

    /// <summary>抓取所有显示器的整屏像素。热键按下瞬间调用一次。</summary>
    public IReadOnlyList<CapturedFrame> CaptureAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            var monitors = MonitorEnumerator.GetMonitors();
            var frames = new List<CapturedFrame>(monitors.Count);

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return frames;
            }

            try
            {
                foreach (var monitor in monitors)
                {
                    var frame = CaptureOne(screenDc, monitor);
                    if (frame is not null)
                    {
                        frames.Add(frame);
                    }
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return frames;
        }
    }

    private CapturedFrame? CaptureOne(IntPtr screenDc, MonitorBounds monitor)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0)
        {
            return null;
        }

        var surface = GetOrCreateSurface(screenDc, monitor);
        if (surface is null)
        {
            return null;
        }

        // 源坐标用虚拟桌面坐标，负坐标（副屏在主屏左侧/上方）同样成立。
        var blt = NativeMethods.BitBlt(
            surface.MemoryDc, 0, 0, monitor.Width, monitor.Height,
            screenDc, monitor.Left, monitor.Top,
            NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

        if (!blt)
        {
            return null;
        }

        var length = monitor.Width * monitor.Height * 4;

        // 复用整屏缓冲：16 MB 级别的数组每次都 new 会持续压 LOH，
        // 连续触发时内存曲线会一路往上走。抓屏是串行的（有 _busy 闸门），复用安全。
        if (surface.Buffer is null || surface.Buffer.Length != length)
        {
            surface.Buffer = new byte[length];
        }

        Marshal.Copy(surface.Bits, surface.Buffer, 0, length);
        return new CapturedFrame(monitor, surface.Buffer);
    }

    private Surface? GetOrCreateSurface(IntPtr screenDc, MonitorBounds monitor)
    {
        var key = $"{monitor.DeviceName}|{monitor.Width}x{monitor.Height}";
        if (_surfaces.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var info = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = monitor.Width,
                biHeight = -monitor.Height,   // 负高度 = top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
                biSizeImage = (uint)(monitor.Width * monitor.Height * 4),
            },
        };

        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            return null;
        }

        var bitmap = NativeMethods.CreateDIBSection(
            screenDc, ref info, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);

        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            NativeMethods.DeleteDC(memoryDc);
            return null;
        }

        var previous = NativeMethods.SelectObject(memoryDc, bitmap);
        var surface = new Surface(memoryDc, bitmap, bits, previous);
        _surfaces[key] = surface;
        return surface;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var surface in _surfaces.Values)
        {
            surface.Dispose();
        }

        _surfaces.Clear();
    }

    private sealed class Surface(IntPtr memoryDc, IntPtr bitmap, IntPtr bits, IntPtr previousObject) : IDisposable
    {
        public IntPtr MemoryDc { get; } = memoryDc;

        public IntPtr Bits { get; } = bits;

        /// <summary>整屏像素缓冲，跨次抓屏复用。下一帧会覆盖它，所以不要跨帧持有。</summary>
        public byte[]? Buffer { get; set; }

        public void Dispose()
        {
            if (previousObject != IntPtr.Zero)
            {
                NativeMethods.SelectObject(MemoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (MemoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(MemoryDc);
            }
        }
    }
}
