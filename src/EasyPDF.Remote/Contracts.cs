using System.Collections.Generic;
using System.Text.Json.Serialization;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>
/// An uploaded or server-made PDF: its id, file name, every page size (points, after the page's /Rotate) and its size in
/// bytes; for a written file also how many pictures were scaled down.
/// </summary>
public sealed record RemoteFileInfo(string Id, string Name, int PageCount, IReadOnlyList<PageSizePt> PageSizes, long Size = 0,
    int ReducedImages = 0);

/// <summary>
/// One page of a document to write: page <see cref="Index"/> of file <see cref="FileId"/>, turned by <see cref="Rotation"/>,
/// with what was placed on it and is not saved yet (all in the page's own space, see <see cref="StampOverlay"/>).
/// </summary>
public sealed record RemotePage(string FileId, int Index, int Rotation, IReadOnlyList<RemoteStamp>? Stamps = null,
    IReadOnlyList<TextOverlay>? Texts = null, IReadOnlyList<RedactionOverlay>? Redactions = null, IReadOnlyList<OcrWord>? OcrWords = null);

/// <summary>A placed picture: its decoded pixels (top-down BGRA, as the browser has them), not the picture file.</summary>
public sealed record RemoteStamp(int Id, int PixelWidth, int PixelHeight, RectPt Rect, int Rotation, bool CoversContent, byte[] Bgra);

/// <summary>
/// Writes the pages based on <see cref="BaseFileId"/> (its bookmarks and metadata are kept) as a new file named
/// <see cref="Name"/>; with <see cref="ReduceImages"/> its pictures are scaled down.
/// </summary>
public sealed record BuildRequest(string BaseFileId, string Name, IReadOnlyList<RemotePage> Pages,
    ImageReduction? ReduceImages = null);

/// <summary>Writes the pages as a new file without bookmarks.</summary>
public sealed record ExtractRequest(string Name, IReadOnlyList<RemotePage> Pages);

/// <summary>Writes the parts of the page list as <c>&lt;BaseName&gt;_partNN.pdf</c>; the answer is a ZIP file with them.</summary>
public sealed record SplitDocumentRequest(IReadOnlyList<RemotePage> Pages, IReadOnlyList<SplitPart> Parts, string BaseName);

/// <summary>A new file with one empty page of this size (points).</summary>
public sealed record BlankRequest(double Width, double Height);

/// <summary>What the browser program needs to know about the server.</summary>
public sealed record ServerConfig(string AuthenticationMode, long MaxUploadBytes, int IdleTimeoutMinutes, int MaxAgeHours);

/// <summary>An error the browser shows in the user's language: <see cref="Code"/> is one of <see cref="ApiErrors"/>.</summary>
public sealed record ApiError(string Code);

/// <summary>Error codes of <see cref="ApiError"/>.</summary>
public static class ApiErrors
{
    public const string NotPdf = "NotPdf";
    public const string TooLarge = "TooLarge";
    public const string QuotaExceeded = "QuotaExceeded";
    public const string PasswordProtected = "PasswordProtected";
    public const string Damaged = "Damaged";
    public const string NotFound = "NotFound";
    public const string TooManyRequests = "TooManyRequests";
    public const string Invalid = "Invalid";
}

/// <summary>Header every changing request must carry (only scripts of the app's own page can set it: protection against CSRF).</summary>
public static class ApiHeaders
{
    public const string Request = "X-App-Request";
    public const string FileName = "X-File-Name";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteFileInfo))]
[JsonSerializable(typeof(BuildRequest))]
[JsonSerializable(typeof(ExtractRequest))]
[JsonSerializable(typeof(BlankRequest))]
[JsonSerializable(typeof(SplitDocumentRequest))]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(IReadOnlyList<OcrWord>))]
[JsonSerializable(typeof(IReadOnlyList<PdfAttachment>))]
[JsonSerializable(typeof(int))]
public sealed partial class RemoteJson : JsonSerializerContext;
