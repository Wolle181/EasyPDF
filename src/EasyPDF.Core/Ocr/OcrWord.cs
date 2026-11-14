using EasyPDF.Core.Documents;

namespace EasyPDF.Core.Ocr;

/// <summary>
/// A recognized word. From <see cref="IOcrEngine"/> the box is in pixels of the recognized image; from
/// <see cref="PageOcr"/> (and <c>PdfSource.ReadWordsAsync</c>) it is in the page's own space like the overlays:
/// page size after its own /Rotate, before the extra rotation, origin top-left, in points.
/// </summary>
/// <param name="Text">The word without spaces.</param>
/// <param name="Box">Axis-aligned box around the word. OCR boxes are taller than the glyphs (about the line height).</param>
/// <param name="Confidence">0..1; 1 for text read from the PDF itself.</param>
/// <param name="Rotation">
/// Clockwise turn of the text inside page space (0, 90, 180, 270), like <see cref="TextOverlay.Rotation"/>: a page that
/// was recognized while shown turned by 90° has its words at 270°.
/// </param>
public sealed record OcrWord(string Text, RectPt Box, float Confidence, int Rotation = 0);
