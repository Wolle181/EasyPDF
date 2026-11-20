using System.Collections.Generic;
using System.Text.Json.Serialization;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>An uploaded or server-made PDF: its id, file name and every page size (points, after the page's /Rotate).</summary>
public sealed record RemoteFileInfo(string Id, string Name, int PageCount, IReadOnlyList<PageSizePt> PageSizes);

/// <summary>One page of a document to write: page <see cref="Index"/> of file <see cref="FileId"/>, turned by <see cref="Rotation"/>.</summary>
public sealed record RemotePage(string FileId, int Index, int Rotation);

/// <summary>Writes the pages based on <see cref="BaseFileId"/> (its bookmarks and metadata are kept) as a new file named <see cref="Name"/>.</summary>
public sealed record BuildRequest(string BaseFileId, string Name, IReadOnlyList<RemotePage> Pages);

/// <summary>Writes the pages as a new file without bookmarks.</summary>
public sealed record ExtractRequest(string Name, IReadOnlyList<RemotePage> Pages);

/// <summary>Writes the parts of the page list as <c>&lt;BaseName&gt;_partNN.pdf</c>; the answer is a ZIP file with them.</summary>
public sealed record SplitDocumentRequest(IReadOnlyList<RemotePage> Pages, IReadOnlyList<EasyPDF.Core.Documents.SplitPart> Parts, string BaseName);

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
