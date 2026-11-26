using SnapOCR.Capture;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>
/// DPI 换算的回归测试。所有 DIP ↔ 物理像素的实现都必须经过 DpiHelper，
/// 一旦有人改错比例（比如漏乘或写成 96/dpi），这里会第一时间挂。
/// </summary>
public class DpiHelperTests
{
    [Theory]
    [InlineData(96, 100, 100)]     // 100%
    [InlineData(120, 100, 125)]    // 125%
    [InlineData(144, 100, 150)]    // 150%
    [InlineData(168, 100, 175)]    // 175%
    [InlineData(192, 100, 200)]    // 200%
    [InlineData(144, 42.5, 63.75)]
    public void DipToPhysical_UsesDpiOver96(int dpi, double dip, double expected)
    {
        Assert.Equal(expected, DpiHelper.DipToPhysical(dip, (uint)dpi), 6);
    }

    [Theory]
    [InlineData(96, 100, 100)]
    [InlineData(144, 150, 100)]
    [InlineData(192, 200, 100)]
    [InlineData(120, 125, 100)]
    public void PhysicalToDip_IsInverse(int dpi, double physical, double expected)
    {
        Assert.Equal(expected, DpiHelper.PhysicalToDip(physical, (uint)dpi), 6);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    [InlineData(216)]
    [InlineData(240)]
    public void RoundTrip_IsLosslessForTypicalValues(int dpi)
    {
        for (var dip = -500.0; dip <= 4000.0; dip += 7.5)
        {
            var physical = DpiHelper.DipToPhysical(dip, (uint)dpi);
            var back = DpiHelper.PhysicalToDip(physical, (uint)dpi);
            Assert.Equal(dip, back, 6);
        }
    }

    [Fact]
    public void NegativeCoordinates_AreScaledCorrectly()
    {
        // 副屏在主屏左侧时会出现负坐标，不能取绝对值。
        Assert.Equal(-150, DpiHelper.DipToPhysicalInt(-100, 144));
        Assert.Equal(-150, DpiHelper.DipToPhysical(-100, 144), 6);
    }

    [Fact]
    public void IntConversion_RoundsAwayFromZero()
    {
        Assert.Equal(150, DpiHelper.DipToPhysicalInt(100, 144));
        Assert.Equal(151, DpiHelper.DipToPhysicalInt(100.5, 144));
        Assert.Equal(-1, DpiHelper.DipToPhysicalInt(-0.5, 96));
    }

    [Fact]
    public void ZeroDpi_FallsBackTo100Percent()
    {
        Assert.Equal(1.0, DpiHelper.ScaleFactor(0), 6);
        Assert.Equal(100, DpiHelper.DipToPhysical(100, 0), 6);
    }

    [Fact]
    public void ScaleFactor_MatchesPercentage()
    {
        Assert.Equal(1.0, DpiHelper.ScaleFactor(96), 6);
        Assert.Equal(1.25, DpiHelper.ScaleFactor(120), 6);
        Assert.Equal(1.5, DpiHelper.ScaleFactor(144), 6);
        Assert.Equal(2.0, DpiHelper.ScaleFactor(192), 6);
    }

    [Fact]
    public void PhysicalToDipInt_RoundsToNearest()
    {
        Assert.Equal(100, DpiHelper.PhysicalToDipInt(150, 144));
        Assert.Equal(101, DpiHelper.PhysicalToDipInt(151, 144));
    }
}
