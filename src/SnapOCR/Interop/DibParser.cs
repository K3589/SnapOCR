using System.Buffers.Binary;
using System.Numerics;

namespace SnapOCR.Interop;

/// <summary>
/// 纯内存 CF_DIB 解析器（无 Win32 句柄依赖，便于单测与边界校验）。
/// 支持 24/32 位 BI_RGB，以及 32 位 BI_BITFIELDS（常见于 CF_DIB / CF_DIBV5）。
/// </summary>
public static class DibParser
{
    /// <summary>单边最大允许像素，防止畸形 DIB 头触发超大内存分配。</summary>
    public const int MaxDimension = 16384;

    private const int BiRgb = 0;
    private const int BiBitfields = 3;

    public static (byte[] Bgra, int Width, int Height)? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(0, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(16, 4));
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(32, 4));

        if (headerSize < 40 || headerSize > data.Length)
        {
            return null;
        }

        if (width <= 0 || width > MaxDimension || rawHeight == 0 || rawHeight == int.MinValue)
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        if (height > MaxDimension || colorsUsed < 0)
        {
            return null;
        }

        var isRgb = compression == BiRgb && bitCount is 24 or 32;
        var isBitfields32 = compression == BiBitfields && bitCount == 32;
        if (!isRgb && !isBitfields32)
        {
            return null;
        }

        uint redMask = 0x00FF0000;
        uint greenMask = 0x0000FF00;
        uint blueMask = 0x000000FF;
        long extraMaskBytes = 0;

        if (isBitfields32)
        {
            if (data.Length < 52)
            {
                return null;
            }

            redMask = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40, 4));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(44, 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(48, 4));

            if (redMask == 0 || greenMask == 0 || blueMask == 0)
            {
                return null;
            }

            // BITMAPINFOHEADER（40 字节）时，3 个 DWORD 掩码紧跟在头后面；
            // BITMAPV4HEADER / BITMAPV5HEADER（>= 52 字节）则已包含在 headerSize 内。
            if (headerSize == 40)
            {
                extraMaskBytes = 12;
            }
        }

        var pixelOffset = (long)headerSize + extraMaskBytes + ((long)colorsUsed * 4);
        var sourceStride = ((((long)width * bitCount) + 31) / 32) * 4;
        var requiredBytes = pixelOffset + (sourceStride * height);
        if (pixelOffset < headerSize || requiredBytes > data.Length)
        {
            return null;
        }

        var outputBytes = (long)width * height * 4;
        if (outputBytes <= 0 || outputBytes > int.MaxValue)
        {
            return null;
        }

        var bottomUp = rawHeight > 0;
        var bytesPerPixel = bitCount / 8;
        var buffer = new byte[(int)outputBytes];
        var pixelData = data.Slice((int)pixelOffset);

        for (var y = 0; y < height; y++)
        {
            var sourceRowIndex = bottomUp ? height - 1 - y : y;
            var row = pixelData.Slice((int)(sourceRowIndex * sourceStride), (int)sourceStride);
            var targetRow = y * width * 4;

            if (isBitfields32)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(x * 4, 4));
                    var targetIndex = targetRow + (x * 4);
                    buffer[targetIndex] = ExtractChannel(pixel, blueMask);
                    buffer[targetIndex + 1] = ExtractChannel(pixel, greenMask);
                    buffer[targetIndex + 2] = ExtractChannel(pixel, redMask);
                    buffer[targetIndex + 3] = 255;
                }
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    var sourceIndex = x * bytesPerPixel;
                    var targetIndex = targetRow + (x * 4);
                    buffer[targetIndex] = row[sourceIndex];         // B
                    buffer[targetIndex + 1] = row[sourceIndex + 1]; // G
                    buffer[targetIndex + 2] = row[sourceIndex + 2]; // R
                    buffer[targetIndex + 3] = 255;                  // A
                }
            }
        }

        return (buffer, width, height);
    }

    private static byte ExtractChannel(uint pixel, uint mask)
    {
        var shift = BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        if (max == 0)
        {
            return 0;
        }

        var value = (pixel & mask) >> shift;
        return max == 255 ? (byte)value : (byte)((value * 255 + (max / 2)) / max);
    }
}
