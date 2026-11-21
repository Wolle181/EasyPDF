using System.IO;
using System.Text;
using System.Xml;

namespace EasyPDF.Server;

/// <summary>An XML file as readable text: indented if it is well-formed, otherwise as it is.</summary>
public static class XmlText
{
    public static string Indented(byte[] content)
    {
        try
        {
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(content), readerSettings);
            // Into UTF-8 bytes, so the XML declaration names the encoding of the text sent
            var output = new MemoryStream();
            var writerSettings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
            using (var writer = XmlWriter.Create(output, writerSettings))
                writer.WriteNode(reader, true);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (XmlException)
        {
            return Decode(content);
        }
    }

    // UTF-8 unless the file starts with another byte order mark
    private static string Decode(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
