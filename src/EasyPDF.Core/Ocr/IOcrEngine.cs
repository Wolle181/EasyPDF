using System;
using System.Collections.Generic;
using System.Threading;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Core.Ocr;

/// <summary>Text recognition on a rendered page. Implementations may be called from any thread, one call at a time or concurrently.</summary>
public interface IOcrEngine : IDisposable
{
    /// <summary>Recognizes the words of <paramref name="image"/>; boxes are in pixels of the image.</summary>
    IReadOnlyList<OcrWord> Recognize(RenderedPage image, CancellationToken token = default);
}
