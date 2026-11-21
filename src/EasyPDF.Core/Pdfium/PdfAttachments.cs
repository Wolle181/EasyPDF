using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PDFiumCore;

namespace EasyPDF.Core.Pdfium;

/// <summary>A file embedded in the PDF (document-level attachment, /EmbeddedFiles).</summary>
/// <param name="Index">Position in pdfium's attachment list (for <see cref="PdfSource.ReadAttachmentAsync"/>).</param>
/// <param name="Name">File name as stored in the PDF (may contain anything; see <see cref="SafeFileName"/>).</param>
/// <param name="Size">Size in bytes.</param>
public sealed record PdfAttachment(int Index, string Name, long Size)
{
    /// <summary>The XML of an electronic invoice: ZUGFeRD, Factur-X or XRechnung (recognized by the file name the standards prescribe).</summary>
    public bool IsEInvoice => PdfAttachments.EInvoiceNames.Contains(Name, StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Name"/> usable as a file name on every system: no folders, no invalid characters, never empty.</summary>
    public string SafeFileName => PdfAttachments.SafeFileName(Name);

    /// <summary>Files that open in the system's program without risk (documents and pictures; no programs, scripts or macros).</summary>
    public bool CanOpen => PdfAttachments.OpenableExtensions.Contains(Path.GetExtension(SafeFileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>An XML file (like the e-invoice): the web version shows it as text in a new browser tab.</summary>
    public bool IsXml => string.Equals(Path.GetExtension(SafeFileName), ".xml", StringComparison.OrdinalIgnoreCase);
}

internal static class PdfAttachments
{
    /// <summary>ZUGFeRD 1 (zugferd-invoice.xml), ZUGFeRD 2 / Factur-X (factur-x.xml, older 2.0 files zugferd-invoice.xml), XRechnung (xrechnung.xml).</summary>
    public static readonly string[] EInvoiceNames = ["factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml"];

    public static readonly string[] OpenableExtensions =
        [".xml", ".pdf", ".txt", ".csv", ".json", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp"];

    public static string SafeFileName(string name)
    {
        var file = name.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        file = new string([.. file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        return file.Length == 0 || file.All(c => c == '.') ? "attachment" : file;
    }

    /// <summary>All attachments of the document; ones pdfium cannot read are left out. Worker thread only.</summary>
    public static List<PdfAttachment> List(FpdfDocumentT doc)
    {
        var list = new List<PdfAttachment>();
        var count = fpdf_attachment.FPDFDocGetAttachmentCount(doc);
        for (var i = 0; i < count; i++)
        {
            var attachment = fpdf_attachment.FPDFDocGetAttachment(doc, i);
            if (attachment == null)
                continue;
            ulong size = 0;
            if (fpdf_attachment.FPDFAttachmentGetFile(attachment, IntPtr.Zero, 0, ref size) == 0)
                continue;
            list.Add(new PdfAttachment(i, Name(attachment), (long)size));
        }
        return list;
    }

    /// <summary>The content of attachment <paramref name="index"/>. Worker thread only.</summary>
    public static byte[] Read(FpdfDocumentT doc, int index)
    {
        var attachment = fpdf_attachment.FPDFDocGetAttachment(doc, index) ?? throw new IOException($"Attachment {index} not found.");
        ulong size = 0;
        if (fpdf_attachment.FPDFAttachmentGetFile(attachment, IntPtr.Zero, 0, ref size) == 0)
            throw new IOException($"Attachment {index} cannot be read.");
        var data = new byte[size];
        if (size == 0)
            return data;
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            ulong written = 0;
            if (fpdf_attachment.FPDFAttachmentGetFile(attachment, handle.AddrOfPinnedObject(), size, ref written) == 0 || written != size)
                throw new IOException($"Attachment {index} cannot be read.");
        }
        finally
        {
            handle.Free();
        }
        return data;
    }

    private static string Name(FpdfAttachmentT attachment)
    {
        var length = fpdf_attachment.FPDFAttachmentGetName(attachment, ref Unsafe.NullRef<ushort>(), 0); // bytes incl. terminator
        if (length <= 2)
            return "";
        var buffer = new ushort[length / 2];
        fpdf_attachment.FPDFAttachmentGetName(attachment, ref buffer[0], length);
        return new string([.. buffer.TakeWhile(c => c != 0).Select(c => (char)c)]);
    }
}
