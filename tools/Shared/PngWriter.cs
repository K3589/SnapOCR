using System.IO.Compression;

namespace SnapOCR.Tools;

/// <summary>
/// 极简 PNG 编码器：只用 BCL 的 ZLibStream，不引任何图像库。
/// 诊断工具用它输出可直接查看的缩略图（BMP 动辄十几 MB，看图工具打不开）。
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>把 BGRA 像素按 <paramref name="scale"/> 抽稀后写成 PNG。</summary>
    public static void Write(string path, byte[] bgra, int width, int height, int scale = 1)
        => WriteRegion(path, bgra, width, height, 0, 0, width, height, scale);

    /// <summary>只写源图的某一块区域（用于放大看局部细节）。</summary>
    public static void WriteRegion(string path, byte[] bgra, int width, int height, int sourceX, int sourceY, int regionWidth, int regionHeight, int scale = 1)
    {
        scale = Math.Max(1, scale);
        var outWidth = Math.Max(1, regionWidth / scale);
        var outHeight = Math.Max(1, regionHeight / scale);
        var stride = outWidth * 4;

        // PNG 是 RGBA 顺序，扫描线前还有一个 filter 字节。
        var raw = new byte[(stride + 1) * outHeight];
        for (var y = 0; y < outHeight; y++)
        {
            var rowStart = y * (stride + 1);
            raw[rowStart] = 0;   // filter = None

            for (var x = 0; x < outWidth; x++)
            {
                var sourceRow = Math.Min(height - 1, sourceY + (y * scale));
                var sourceColumn = Math.Min(width - 1, sourceX + (x * scale));
                var source = ((sourceRow * width) + sourceColumn) * 4;
                var target = rowStart + 1 + (x * 4);
                raw[target] = bgra[source + 2];      // R
                raw[target + 1] = bgra[source + 1];  // G
                raw[target + 2] = bgra[source];      // B
                raw[target + 3] = 255;               // A
            }
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = buffer.ToArray();
        }

        using var stream = File.Create(path);
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var header = new byte[13];
        WriteBigEndian(header, 0, outWidth);
        WriteBigEndian(header, 4, outHeight);
        header[8] = 8;    // bit depth
        header[9] = 6;    // color type RGBA
        WriteChunk(stream, "IHDR", header);
        WriteChunk(stream, "IDAT", compressed);
        WriteChunk(stream, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);

        var typeBytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            typeBytes[i] = (byte)type[i];
        }

        stream.Write(typeBytes);
        stream.Write(data);

        var crc = UpdateCrc(0xFFFFFFFFu, typeBytes);
        crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;

        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint UpdateCrc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
