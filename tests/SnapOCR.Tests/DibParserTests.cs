using System.Buffers.Binary;
using SnapOCR.Interop;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>CF_DIB 纯内存解析与边界校验回归测试。</summary>
public class DibParserTests
{
    [Fact]
    public void TryParse_32BitBottomUpRgb_FlipsRowsAndSetsOpaqueAlpha()
    {
        // 2x2 bottom-up 32bpp：第 0 行在 DIB 底部，第 1 行在 DIB 顶部。
        var dib = BuildHeader(width: 2, height: 2, bitCount: 32, compression: 0, payloadBytes: 2 * 2 * 4);
        // bottom row (y=1): (B=10, G=20, R=30, A=0), (B=40, G=50, R=60, A=0)
        dib[40] = 10; dib[41] = 20; dib[42] = 30; dib[43] = 0;
        dib[44] = 40; dib[45] = 50; dib[46] = 60; dib[47] = 0;
        // top row (y=0): (B=70, G=80, R=90, A=0), (B=100, G=110, R=120, A=0)
        dib[48] = 70; dib[49] = 80; dib[50] = 90; dib[51] = 0;
        dib[52] = 100; dib[53] = 110; dib[54] = 120; dib[55] = 0;

        var parsed = DibParser.TryParse(dib);
        Assert.NotNull(parsed);
        var (bgra, width, height) = parsed.Value;
        Assert.Equal(2, width);
        Assert.Equal(2, height);

        // 输出应为 top-down 且 Alpha=255
        Assert.Equal(new byte[] { 70, 80, 90, 255, 100, 110, 120, 255, 10, 20, 30, 255, 40, 50, 60, 255 }, bgra);
    }

    [Fact]
    public void TryParse_24BitTopDownRgbWithRowPadding_ParsesCorrectly()
    {
        // width=1, 24bpp → 每行 3 字节 + 1 字节 4 字节对齐填充 = stride 4
        var dib = BuildHeader(width: 1, height: -2, bitCount: 24, compression: 0, payloadBytes: 8);
        dib[40] = 1; dib[41] = 2; dib[42] = 3; dib[43] = 0xAA;
        dib[44] = 4; dib[45] = 5; dib[46] = 6; dib[47] = 0xBB;

        var parsed = DibParser.TryParse(dib);
        Assert.NotNull(parsed);
        var (bgra, width, height) = parsed.Value;
        Assert.Equal(1, width);
        Assert.Equal(2, height);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, bgra);
    }

    [Fact]
    public void TryParse_32BitBitfields_ExtractsRgbChannels()
    {
        // headerSize=40 + 12 字节掩码 + 4 字节像素（1x1）
        var dib = BuildHeader(width: 1, height: -1, bitCount: 32, compression: 3, payloadBytes: 12 + 4);
        // RGBX 顺序掩码（R 在低字节，B 在高字节）
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(40, 4), 0x000000FFu); // R
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(44, 4), 0x0000FF00u); // G
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(48, 4), 0x00FF0000u); // B
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(52, 4), 0x00112233u); // B=0x11, G=0x22, R=0x33

        var parsed = DibParser.TryParse(dib);
        Assert.NotNull(parsed);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 255 }, parsed.Value.Bgra);
    }

    [Fact]
    public void TryParse_RejectsTruncatedAndMalformedHeaders()
    {
        Assert.Null(DibParser.TryParse(Array.Empty<byte>()));
        Assert.Null(DibParser.TryParse(new byte[39]));

        // 截断的像素区
        var truncated = BuildHeader(width: 10, height: 10, bitCount: 32, compression: 0, payloadBytes: 16);
        Assert.Null(DibParser.TryParse(truncated));

        // rawHeight = int.MinValue 不能抛 OverflowException
        var minHeight = BuildHeader(width: 1, height: int.MinValue, bitCount: 32, compression: 0, payloadBytes: 4);
        Assert.Null(DibParser.TryParse(minHeight));

        // 超过安全上限的超大尺寸
        var huge = BuildHeader(width: 50000, height: 50000, bitCount: 32, compression: 0, payloadBytes: 4);
        Assert.Null(DibParser.TryParse(huge));
    }

    private static byte[] BuildHeader(int width, int height, short bitCount, int compression, int payloadBytes)
    {
        var buffer = new byte[40 + payloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), height);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(14, 2), bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), compression);
        return buffer;
    }
}
