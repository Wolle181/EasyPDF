using System;
using System.Collections.Generic;
using System.Threading;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>
/// The OCR engine of the browser program: there is none (no ONNX Runtime in the browser). Every page is a server file
/// (<see cref="IServerOcr"/>), so <see cref="PageOcr"/> never asks it.
/// </summary>
public sealed class ServerOcrOnly : IOcrEngine
{
    public IReadOnlyList<OcrWord> Recognize(RenderedPage image, CancellationToken token = default) =>
        throw new NotSupportedException("Text recognition runs on the server.");

    public void Dispose()
    {
    }
}
