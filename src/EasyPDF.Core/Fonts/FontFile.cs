using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EasyPDF.Core.Fonts;

/// <summary>
/// An installed font file that can be embedded into a PDF: a TrueType font (outlines in <c>glyf</c>, .ttf or .otf) whose
/// licence allows embedding. <paramref name="Ascent"/> is the share of the line height above the baseline (hhea:
/// ascender / (ascender − descender + line gap)), the way text measuring lays out lines; <paramref name="Weight"/> the
/// OS/2 weight class (400 regular, 700 bold).
/// </summary>
public sealed record FontFace(string Path, string Family, string Style, string PostScriptName, bool Bold, bool Italic, double Ascent, int Weight = 400);

/// <summary>
/// Reads what <see cref="FontLibrary"/> needs from a font file: the tables <c>name</c> (family, style, PostScript name),
/// <c>OS/2</c> (bold, italic, embedding permission), <c>hhea</c> (ascent) and whether it has TrueType outlines. Font
/// collections (.ttc) and fonts with CFF outlines are left out: pdfium embeds a single TrueType font only.
/// </summary>
public static class FontFile
{
    /// <summary>The face in the file, or null if it is not a TrueType font that may be embedded (or not readable).</summary>
    public static FontFace? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    internal static FontFace? Read(Stream stream, string path)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var version = U32(reader);
        if (version != 0x00010000 && version != 0x74727565) // TrueType ("true" on old Macs); "OTTO" = CFF, "ttcf" = collection
            return null;
        var count = U16(reader);
        reader.ReadBytes(6);
        var tables = new Dictionary<string, (uint Offset, uint Length)>();
        for (var i = 0; i < count; i++)
        {
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            U32(reader); // checksum
            tables[tag] = (U32(reader), U32(reader));
        }
        if (!tables.ContainsKey("glyf") || !tables.TryGetValue("name", out var name) || !tables.TryGetValue("head", out var head))
            return null;

        // OS/2: fsType (embedding licence) and fsSelection (italic, bold); without the table only the head flags count
        bool bold, italic;
        var weight = 400;
        if (tables.TryGetValue("OS/2", out var os2))
        {
            stream.Position = os2.Offset + 4;
            weight = U16(reader);
            var fsType = U16(reader);
            if ((fsType & 0x000F) == 0x0002)
                return null; // "restricted licence embedding": the font must not be embedded
            stream.Position = os2.Offset + 62;
            var selection = U16(reader);
            italic = (selection & 0x0001) != 0;
            bold = (selection & 0x0020) != 0 || weight >= 600;
        }
        else
        {
            stream.Position = head.Offset + 44;
            var macStyle = U16(reader);
            bold = (macStyle & 1) != 0;
            italic = (macStyle & 2) != 0;
            weight = bold ? 700 : 400;
        }

        var ascent = 0.8;
        if (tables.TryGetValue("hhea", out var hhea))
        {
            stream.Position = hhea.Offset + 4;
            var (asc, desc, gap) = ((short)U16(reader), (short)U16(reader), (short)U16(reader));
            var height = asc - desc + gap;
            if (asc > 0 && height > 0)
                ascent = (double)asc / height;
        }

        var names = ReadNames(stream, reader, name.Offset);
        var family = names.GetValueOrDefault(16) ?? names.GetValueOrDefault(1);
        var style = names.GetValueOrDefault(17) ?? names.GetValueOrDefault(2) ?? "";
        if (string.IsNullOrWhiteSpace(family))
            return null;
        var postScript = names.GetValueOrDefault(6) ?? (family + "-" + style).Replace(" ", "");
        return new FontFace(path, family.Trim(), style.Trim(), postScript.Trim(), bold, italic, ascent, weight is >= 1 and <= 1000 ? weight : 400);
    }

    /// <summary>Name records by name id: the Windows English (or any Windows Unicode) entry, else the Mac Roman one.</summary>
    private static Dictionary<int, string> ReadNames(Stream stream, BinaryReader reader, uint offset)
    {
        stream.Position = offset;
        U16(reader); // format
        var count = U16(reader);
        var strings = offset + U16(reader);
        var best = new Dictionary<int, (int Rank, string Value)>();
        for (var i = 0; i < count; i++)
        {
            var (platform, encoding, language, id) = (U16(reader), U16(reader), U16(reader), U16(reader));
            var (length, start) = (U16(reader), U16(reader));
            if (id is not (1 or 2 or 6 or 16 or 17))
                continue;
            var rank = platform == 3 && encoding is 1 or 0 ? (language == 0x0409 ? 3 : 2) : platform == 1 && encoding == 0 ? 1 : 0;
            if (rank == 0 || best.TryGetValue(id, out var known) && known.Rank >= rank)
                continue;
            var back = stream.Position;
            stream.Position = strings + start;
            var bytes = reader.ReadBytes(length);
            stream.Position = back;
            best[id] = (rank, platform == 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes));
        }
        var result = new Dictionary<int, string>();
        foreach (var (id, entry) in best)
            result[id] = entry.Value;
        return result;
    }

    private static ushort U16(BinaryReader r)
    {
        var b = r.ReadBytes(2);
        if (b.Length < 2)
            throw new EndOfStreamException();
        return (ushort)(b[0] << 8 | b[1]);
    }

    private static uint U32(BinaryReader r)
    {
        var b = r.ReadBytes(4);
        if (b.Length < 4)
            throw new EndOfStreamException();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}
