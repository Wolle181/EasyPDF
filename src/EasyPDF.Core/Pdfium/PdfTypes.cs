using System.Collections.Generic;

namespace EasyPDF.Core.Pdfium;

/// <summary>Page size in PDF points (1/72 inch), including the page's own /Rotate.</summary>
public readonly record struct PageSizePt(double Width, double Height)
{
    public PageSizePt Rotated(int degrees) => ((degrees % 360) + 360) % 360 is 90 or 270 ? new PageSizePt(Height, Width) : this;
}

/// <summary>A page rendered to a top-down BGRA buffer (opaque, white background).</summary>
public sealed class RenderedPage(int width, int height, int stride, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public byte[] Pixels { get; } = pixels;
}

public sealed class OutlineNode
{
    public string Title { get; init; } = "";

    /// <summary>Zero-based page index in the source file, or -1 if the bookmark has no page target.</summary>
    public int SourcePageIndex { get; init; } = -1;

    public List<OutlineNode> Children { get; } = [];
}

public sealed class PdfOpenException(string path, ulong errorCode)
    : System.Exception($"Cannot open '{path}': {Describe(errorCode)}")
{
    /// <summary>pdfium error code (FPDF_ERR_*).</summary>
    public ulong ErrorCode { get; } = errorCode;

    public bool IsPasswordProtected => ErrorCode == 4;

    private static string Describe(ulong code) => code switch
    {
        2 => "file not found or could not be opened",
        3 => "not a valid PDF or damaged file",
        4 => "password protected",
        5 => "unsupported security scheme",
        6 => "page not found or content error",
        _ => $"unknown error {code}",
    };
}
