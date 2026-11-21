using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EasyPDF.Core.Ocr;

/// <summary>
/// A source whose pages are recognized where the file is (the web version: on the server, which has the OCR models);
/// <see cref="PageOcr"/> asks it instead of rendering the page and running a local engine.
/// </summary>
public interface IServerOcr
{
    /// <summary>The words of the page as <see cref="PageOcr.RecognizeAsync"/> returns them (page space).</summary>
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(int pageIndex, int extraRotation, CancellationToken token = default);
}
