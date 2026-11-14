using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPDF.Core.Stamps;

/// <summary>
/// The definition of a stamp lives inside its PNG file (an iTXt chunk with JSON), so a stamp stays one file: it can be
/// renamed, copied and passed on, and everything that handles stamp pictures works unchanged. A program that rewrites
/// the picture may drop the chunk; then it is a plain picture again.
/// </summary>
public static class StampFile
{
    /// <summary>Keyword of the chunk (the technical name, it stays when the product is renamed).</summary>
    public const string Keyword = "EasyPDF.Stamp";

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] KeywordBytes = Encoding.Latin1.GetBytes(Keyword);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The definition in the file; null for a plain picture and anything unusable (a stamp stays usable without its fields).</summary>
    public static StampDefinition? ReadDefinition(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data))
            return null;
        for (var pos = Signature.Length; pos + 12 <= data.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            if (length < 0 || pos + 12L + length > data.Length)
                return null;
            if (data.Slice(pos + 4, 4).SequenceEqual("iTXt"u8) && TextOfOurChunk(data.Slice(pos + 8, length)) is { } text)
                return Parse(text);
            pos += 12 + length;
        }
        return null;
    }

    /// <summary>The file with <paramref name="definition"/> in it, instead of one it had; without fields it becomes a plain picture.</summary>
    public static byte[] WriteDefinition(byte[] png, StampDefinition? definition)
    {
        byte[]? chunk = null;
        if (definition is { Fields.Count: > 0 })
        {
            // Keyword, no compression, no language, no translated keyword, then the text in UTF-8
            var content = KeywordBytes.Concat(new byte[] { 0, 0, 0, 0, 0 })
                .Concat(JsonSerializer.SerializeToUtf8Bytes(definition, JsonOptions)).ToArray();
            chunk = Chunk("iTXt"u8, content);
        }
        return Replace(png, (type, content) => type.SequenceEqual("iTXt"u8) && TextOfOurChunk(content) != null, chunk);
    }

    /// <summary>The file with the resolution stored in it, which gives the size a stamp is placed in (see <see cref="StampLibrary.ReadDpi"/>).</summary>
    public static byte[] WriteDpi(byte[] png, double dpi)
    {
        var content = new byte[9];
        var perMetre = (uint)Math.Round(dpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(content, perMetre);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4), perMetre);
        content[8] = 1; // unit: metre
        return Replace(png, (type, _) => type.SequenceEqual("pHYs"u8), Chunk("pHYs"u8, content));
    }

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    private static string? TextOfOurChunk(ReadOnlySpan<byte> content)
    {
        var rest = KeywordBytes.Length + 1;
        if (content.Length < rest + 4 || !content[..KeywordBytes.Length].SequenceEqual(KeywordBytes) || content[KeywordBytes.Length] != 0)
            return null;
        if (content[rest] != 0) // compressed: never written by this program
            return null;
        var tags = content[(rest + 2)..]; // skips the compression flag and method
        var language = tags.IndexOf((byte)0);
        if (language < 0)
            return null;
        var translated = tags[(language + 1)..].IndexOf((byte)0);
        return translated < 0 ? null : Encoding.UTF8.GetString(tags[(language + 1 + translated + 1)..]);
    }

    private static StampDefinition? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<StampDefinition>(json, JsonOptions);
            // Null entries and fields that have no area or lie outside the picture can't be drawn
            var fields = parsed?.Fields?.Where(f => f is { Width: > 0, Height: > 0, X: >= 0, Y: >= 0, Text.Length: > 0 } && f.X + f.Width <= 1.0001 && f.Y + f.Height <= 1.0001).ToList();
            return fields is { Count: > 0 } ? new StampDefinition { Fields = fields } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A copy without the chunks <paramref name="remove"/> selects and with <paramref name="insert"/> right after the header.</summary>
    private static byte[] Replace(byte[] png, ChunkFilter remove, byte[]? insert)
    {
        if (!IsPng(png))
            throw new InvalidDataException("Not a PNG file.");
        using var result = new MemoryStream(png.Length + (insert?.Length ?? 0));
        result.Write(Signature);
        for (var pos = Signature.Length; pos + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || pos + 12L + length > png.Length)
                throw new InvalidDataException("Damaged PNG file.");
            var type = png.AsSpan(pos + 4, 4);
            if (!remove(type, png.AsSpan(pos + 8, length)))
                result.Write(png, pos, 12 + length);
            if (type.SequenceEqual("IHDR"u8) && insert != null)
                result.Write(insert);
            pos += 12 + length;
        }
        return result.ToArray();
    }

    private delegate bool ChunkFilter(ReadOnlySpan<byte> type, ReadOnlySpan<byte> content);

    private static byte[] Chunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> content)
    {
        var chunk = new byte[12 + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)content.Length);
        type.CopyTo(chunk.AsSpan(4));
        content.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + content.Length), Crc32(chunk.AsSpan(4, 4 + content.Length)));
        return chunk;
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    })];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
