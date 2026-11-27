using SnapOCR.Capture;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>虚拟桌面坐标、多屏交集与像素裁剪逻辑回归测试。</summary>
public class PixelRectAndCropTests
{
    [Fact]
    public void FromCorners_NormalizesReverseAndNegativeDrags()
    {
        var forward = PixelRect.FromCorners(100, 200, 350, 450);
        Assert.Equal(new PixelRect(100, 200, 250, 250), forward);

        var reverse = PixelRect.FromCorners(-100, -50, -400, -200);
        Assert.Equal(new PixelRect(-400, -200, 300, 150), reverse);

        var zero = PixelRect.FromCorners(50, 50, 50, 100);
        Assert.True(zero.IsEmpty);
    }

    [Fact]
    public void MonitorBounds_Intersect_HandlesNegativeAndDisjointRects()
    {
        var leftMonitor = new MonitorBounds(-1920, 0, 1920, 1080, 96, false, @"\\.\DISPLAY2");

        // 跨副屏和主屏的框选，交集只保留副屏范围内部分
        var crossScreen = new PixelRect(-100, 100, 300, 200);
        Assert.Equal(new PixelRect(-100, 100, 100, 200), leftMonitor.Intersect(crossScreen));

        // 完全落在主屏，交集为空
        var onPrimary = new PixelRect(0, 100, 200, 200);
        Assert.True(leftMonitor.Intersect(onPrimary).IsEmpty);
    }

    [Fact]
    public void CapturedFrame_Crop_ExtractsExactPixelsWithNegativeMonitorOrigin()
    {
        // 构造一块位于 (-100, -50) 的 4×3 屏幕，每个像素写入唯一 (B, G, R, A)
        var monitor = new MonitorBounds(-100, -50, 4, 3, 144, false, @"\\.\DISPLAY2");
        var pixels = new byte[4 * 3 * 4];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var offset = ((y * 4) + x) * 4;
                pixels[offset] = (byte)(x + 1);
                pixels[offset + 1] = (byte)(y + 10);
                pixels[offset + 2] = (byte)(x * 10 + y);
                pixels[offset + 3] = 255;
            }
        }

        var frame = new CapturedFrame(monitor, pixels);

        // 裁取局部 (1, 1) 到 (3, 3)，对应虚拟桌面 (-99, -49) 宽高 2×2
        var cropped = frame.Crop(new PixelRect(-99, -49, 2, 2));
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(8, cropped.Stride);

        // (1,1), (2,1), (1,2), (2,2)
        Assert.Equal(
            new byte[]
            {
                2, 11, 11, 255, 3, 11, 21, 255,
                2, 12, 12, 255, 3, 12, 22, 255,
            },
            cropped.Bgra);
    }

    [Fact]
    public void CapturedFrame_Crop_ClampsOutOfBoundsAndReturnsEmptyWhenDisjoint()
    {
        var monitor = new MonitorBounds(0, 0, 4, 3, 96, true, @"\\.\DISPLAY1");
        var frame = new CapturedFrame(monitor, new byte[4 * 3 * 4]);

        var clamped = frame.Crop(new PixelRect(-2, -1, 4, 3));
        Assert.Equal(2, clamped.Width);
        Assert.Equal(2, clamped.Height);

        var outside = frame.Crop(new PixelRect(10, 10, 5, 5));
        Assert.True(outside.IsEmpty);
    }
}
