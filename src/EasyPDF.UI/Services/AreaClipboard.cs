namespace EasyPDF.App.Services;

/// <summary>
/// A part of a page copied with the copy-area tool: the picture as it was shown (PNG for the screen, straight BGRA for
/// saving, <see cref="EasyPDF.Core.Documents.StampOverlay"/>) and its size on the page in points.
/// </summary>
public sealed record CopiedArea(byte[] Png, int PixelWidth, int PixelHeight, byte[] Bgra, double WidthPt, double HeightPt);

/// <summary>The copied area, shared by all windows of the program (copy in one document, paste in another).</summary>
public sealed class AreaClipboard
{
    public CopiedArea? Current { get; set; }
}
