using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Server.Storage;

/// <summary>
/// Text recognition for the browser: one engine (models loaded on first use, about 0.3 s), one page at a time - OCR takes
/// a few seconds of full CPU per page, several at once would only slow each other down.
/// </summary>
public sealed class OcrService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IOcrEngine? _engine;

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(IPdfSource source, int pageIndex, int rotation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _engine ??= new RapidOcrEngine();
            return await PageOcr.RecognizeAsync(_engine, source, pageIndex, rotation, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _gate.Dispose();
    }
}
