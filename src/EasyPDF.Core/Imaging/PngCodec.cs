using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Imaging;

/// <summary>
/// Minimal PNG writer and reader for rendered pages, so the web version can send them over the network without a native
/// image library on either side. Writes 8-bit RGB (rendered pages are opaque) with the "Up" filter; reads 8-bit grey,
/// RGB and RGBA (non-interlaced, all five filters), which covers what <see cref="Encode"/> writes.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>The page as PNG (the alpha channel is dropped: rendered pages are opaque on white).</summary>
    public static byte[] Encode(RenderedPage page, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, page.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], page.Height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type RGB
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);

        var rowBytes = page.Width * 3;
        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, level, leaveOpen: true))
            {
                var row = new byte[rowBytes + 1];
                var previous = new byte[rowBytes];
                var current = new byte[rowBytes];
                for (var y = 0; y < page.Height; y++)
                {
                    var src = y * page.Stride;
                    for (int x = 0, d = 0; x < page.Width; x++, d += 3)
                    {
                        var s = src + x * 4; // BGRA
                        current[d] = page.Pixels[s + 2];
                        current[d + 1] = page.Pixels[s + 1];
                        current[d + 2] = page.Pixels[s];
                    }
                    row[0] = 2; // Up
                    for (var i = 0; i < rowBytes; i++)
                        row[i + 1] = (byte)(current[i] - previous[i]);
                    zlib.Write(row);
                    (previous, current) = (current, previous);
                }
            }
            WriteChunk(output, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Reads a PNG into a top-down BGRA page (opaque).</summary>
    public static RenderedPage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG image.");
        var pos = 8;
        int width = 0, height = 0, channels = 0;
        using var compressed = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            var type = System.Text.Encoding.ASCII.GetString(png.Slice(pos + 4, 4));
            if (length < 0 || pos + 12 + length > png.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            var data = png.Slice(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[12] != 0)
                        throw new InvalidDataException("Only 8-bit non-interlaced PNG is supported.");
                    channels = data[9] switch
                    {
                        0 => 1,
                        2 => 3,
                        6 => 4,
                        _ => throw new InvalidDataException("Unsupported PNG colour type."),
                    };
                    if (width <= 0 || height <= 0 || (long)width * height > 100_000_000)
                        throw new InvalidDataException("Invalid PNG size.");
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
            }
            pos += 12 + length;
            if (type == "IEND")
                break;
        }
        if (channels == 0)
            throw new InvalidDataException("PNG without header.");

        var rowBytes = width * channels;
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var previous = new byte[rowBytes];
        var current = new byte[rowBytes];
        var filterByte = new byte[1];
        for (var y = 0; y < height; y++)
        {
            zlib.ReadExactly(filterByte);
            zlib.ReadExactly(current);
            Unfilter(filterByte[0], current, previous, channels);
            var dst = y * stride;
            for (int x = 0, s = 0; x < width; x++, s += channels, dst += 4)
            {
                if (channels == 1)
                {
                    pixels[dst] = pixels[dst + 1] = pixels[dst + 2] = current[s];
                }
                else
                {
                    pixels[dst] = current[s + 2];
                    pixels[dst + 1] = current[s + 1];
                    pixels[dst + 2] = current[s];
                }
                pixels[dst + 3] = 255;
            }
            (previous, current) = (current, previous);
        }
        return new RenderedPage(width, height, stride, pixels);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (var i = bpp; i < row.Length; i++)
                    row[i] += row[i - bpp];
                return;
            case 2:
                for (var i = 0; i < row.Length; i++)
                    row[i] += previous[i];
                return;
            case 3:
                for (var i = 0; i < row.Length; i++)
                    row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + previous[i]) / 2);
                return;
            case 4:
                for (var i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                return;
            default:
                throw new InvalidDataException("Invalid PNG filter.");
        }
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
