using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Documents;
using EasyPDF.Core.Imaging;
using EasyPDF.Core.Pdfium;
using EasyPDF.Remote;
using EasyPDF.Server.Security;
using EasyPDF.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace EasyPDF.Server;

/// <summary>The REST API of the web version (all under /api; the owner comes from <see cref="OwnerMiddleware"/>).</summary>
public static class Api
{
    public const string UploadPolicy = "uploads";

    /// <summary>Longest time one pdfium job (render, write) may take before the request is cancelled.</summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/config", (IOptions<ServerOptions> options) =>
        {
            var o = options.Value;
            return Results.Json(new ServerConfig(o.Authentication.Mode.ToString(), o.Storage.MaxUploadBytes, o.Storage.IdleTimeoutMinutes,
                o.Storage.MaxAgeHours), RemoteJson.Default.ServerConfig);
        });
        api.MapPost("/files", UploadAsync).RequireRateLimiting(UploadPolicy);
        api.MapGet("/files/{id}/pages/{page:int}", RenderAsync);
        api.MapGet("/files/{id}/pages/{page:int}/area", RenderAreaAsync);
        api.MapGet("/files/{id}/pages/{page:int}/words", WordsAsync);
        api.MapGet("/files/{id}/pages/{page:int}/chars", CharsAsync);
        api.MapGet("/files/{id}/attachments", AttachmentsAsync);
        api.MapGet("/files/{id}/attachments/{index:int}", AttachmentAsync);
        api.MapGet("/files/{id}/content", Content);
        api.MapDelete("/files/{id}", DeleteAsync);
        api.MapPost("/build", BuildAsync).RequireRateLimiting(UploadPolicy);
        api.MapPost("/extract", ExtractAsync).RequireRateLimiting(UploadPolicy);
        api.MapPost("/blank", BlankAsync).RequireRateLimiting(UploadPolicy);
    }

    private static IResult Error(string code, int status) => Results.Json(new ApiError(code), RemoteJson.Default.ApiError, statusCode: status);

    private static IResult NotFound() => Error(ApiErrors.NotFound, StatusCodes.Status404NotFound);

    private static CancellationTokenSource Job(HttpContext context)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(JobTimeout);
        return cts;
    }

    private static async Task<IResult> UploadAsync(HttpContext context, FileStore files, OpenDocuments documents, IOptions<ServerOptions> options)
    {
        var owner = OwnerKeys.Of(context);
        var max = options.Value.Storage.MaxUploadBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = max + 1;
        if (context.Request.ContentLength > max)
            return Error(ApiErrors.TooLarge, StatusCodes.Status413PayloadTooLarge);
        if (context.Request.ContentLength is { } length && !files.HasRoom(owner, length))
            return Error(ApiErrors.QuotaExceeded, StatusCodes.Status507InsufficientStorage);

        var name = FileStore.CleanName(Uri.UnescapeDataString(context.Request.Headers[ApiHeaders.FileName].ToString()));
        using var job = Job(context);
        string path;
        try
        {
            path = await files.WriteUploadAsync(owner, context.Request.Body, max, job.Token);
        }
        catch (FileTooLargeException)
        {
            return Error(ApiErrors.TooLarge, StatusCodes.Status413PayloadTooLarge);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return Error(ApiErrors.TooLarge, StatusCodes.Status413PayloadTooLarge);
        }
        if (!LooksLikePdf(path))
        {
            FileStore.TryDeleteFile(path);
            return Error(ApiErrors.NotPdf, StatusCodes.Status415UnsupportedMediaType);
        }
        StoredFile file;
        try
        {
            file = files.Register(owner, path, name);
        }
        catch (QuotaExceededException)
        {
            return Error(ApiErrors.QuotaExceeded, StatusCodes.Status507InsufficientStorage);
        }
        try
        {
            return Results.Json(await documents.DescribeAsync(file, job.Token), RemoteJson.Default.RemoteFileInfo);
        }
        catch (PdfOpenException ex)
        {
            files.Delete(owner, file.Id);
            return Error(ex.IsPasswordProtected ? ApiErrors.PasswordProtected : ApiErrors.Damaged, StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>The PDF header must be within the first 1024 bytes (as readers accept it).</summary>
    internal static bool LooksLikePdf(string path)
    {
        Span<byte> head = stackalloc byte[1024];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return head[..read].IndexOf("%PDF-"u8) >= 0;
    }

    private static async Task<(StoredFile? File, PdfSource? Source)> OpenAsync(HttpContext context, string id, FileStore files,
        OpenDocuments documents, CancellationToken token)
    {
        var file = files.Get(OwnerKeys.Of(context), id);
        if (file == null)
            return (null, null);
        return (file, await documents.GetAsync(file, token));
    }

    private static async Task<IResult> RenderAsync(HttpContext context, string id, int page, int w, int h, int rot, FileStore files,
        OpenDocuments documents, IOptions<ServerOptions> options)
    {
        if (w < 1 || h < 1 || (long)w * h > options.Value.RateLimits.MaxRenderPixels)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        if (page < 0 || page >= source.PageCount)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        var rendered = await source.RenderAsync(page, w, h, rot, PdfiumWorker.PriorityVisible, job.Token);
        return Results.Bytes(PngCodec.Encode(rendered), "image/png");
    }

    private static async Task<IResult> RenderAreaAsync(HttpContext context, string id, int page, int pw, int ph, int rot, int x, int y, int w, int h,
        FileStore files, OpenDocuments documents, IOptions<ServerOptions> options)
    {
        var max = options.Value.RateLimits.MaxRenderPixels;
        if (w < 1 || h < 1 || pw < 1 || ph < 1 || (long)w * h > max || x < 0 || y < 0 || x + w > pw || y + h > ph)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        if (page < 0 || page >= source.PageCount)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        var rendered = await source.RenderAreaAsync(page, pw, ph, rot, x, y, w, h, PdfiumWorker.PriorityVisible, job.Token);
        return Results.Bytes(PngCodec.Encode(rendered), "image/png");
    }

    private static async Task<IResult> WordsAsync(HttpContext context, string id, int page, FileStore files, OpenDocuments documents)
    {
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        if (page < 0 || page >= source.PageCount)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        return Results.Json(await source.ReadWordsAsync(page, job.Token), RemoteJson.Default.IReadOnlyListOcrWord);
    }

    private static async Task<IResult> CharsAsync(HttpContext context, string id, int page, FileStore files, OpenDocuments documents)
    {
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        if (page < 0 || page >= source.PageCount)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        return Results.Json(await source.CountTextCharsAsync(page, job.Token), RemoteJson.Default.Int32);
    }

    private static async Task<IResult> AttachmentsAsync(HttpContext context, string id, FileStore files, OpenDocuments documents)
    {
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        return Results.Json(await source.ListAttachmentsAsync(job.Token), RemoteJson.Default.IReadOnlyListPdfAttachment);
    }

    private static async Task<IResult> AttachmentAsync(HttpContext context, string id, int index, FileStore files, OpenDocuments documents)
    {
        using var job = Job(context);
        var (_, source) = await OpenAsync(context, id, files, documents, job.Token);
        if (source == null)
            return NotFound();
        var list = await source.ListAttachmentsAsync(job.Token);
        if (!list.Any(a => a.Index == index))
            return NotFound();
        return Results.Bytes(await source.ReadAttachmentAsync(index, job.Token), "application/octet-stream");
    }

    private static IResult Content(HttpContext context, string id, FileStore files)
    {
        var file = files.Get(OwnerKeys.Of(context), id);
        return file == null ? NotFound() : Results.File(file.Path, "application/pdf", file.Name);
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, string id, FileStore files, OpenDocuments documents)
    {
        var file = files.Delete(OwnerKeys.Of(context), id);
        if (file == null)
            return NotFound();
        await documents.CloseAsync(file.Id);
        return Results.NoContent();
    }

    private static async Task<(List<PageEntry>? Pages, Dictionary<string, PdfSource>? Sources)> PagesAsync(HttpContext context,
        IReadOnlyList<RemotePage>? pages, FileStore files, OpenDocuments documents, CancellationToken token)
    {
        if (pages == null || pages.Count == 0 || pages.Count > 100_000)
            return (null, null);
        var owner = OwnerKeys.Of(context);
        var sources = new Dictionary<string, PdfSource>(StringComparer.Ordinal);
        var result = new List<PageEntry>(pages.Count);
        var id = 0;
        foreach (var page in pages)
        {
            if (!sources.TryGetValue(page.FileId, out var source))
            {
                var file = files.Get(owner, page.FileId);
                if (file == null)
                    return (null, null);
                source = await documents.GetAsync(file, token);
                sources[page.FileId] = source;
            }
            if (page.Index < 0 || page.Index >= source.PageCount || page.Rotation % 90 != 0)
                return (null, null);
            result.Add(new PageEntry(++id, source, page.Index, page.Rotation));
        }
        return (result, sources);
    }

    private static async Task<IResult> BuildAsync(HttpContext context, BuildRequest request, FileStore files, OpenDocuments documents)
    {
        var owner = OwnerKeys.Of(context);
        using var job = Job(context);
        var (pages, sources) = await PagesAsync(context, request.Pages, files, documents, job.Token);
        if (pages == null || sources == null)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        if (!sources.TryGetValue(request.BaseFileId, out var baseSource))
        {
            var baseFile = files.Get(owner, request.BaseFileId);
            if (baseFile == null)
                return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
            baseSource = await documents.GetAsync(baseFile, job.Token);
        }
        var target = files.NewPath(owner);
        var (temp, _) = await PdfSaver.WriteDocumentAsync(pages, baseSource, target, null, job.Token);
        File.Move(temp, target, overwrite: true);
        return await RegisterAsync(owner, target, request.Name, files, documents, job.Token);
    }

    private static async Task<IResult> ExtractAsync(HttpContext context, ExtractRequest request, FileStore files, OpenDocuments documents)
    {
        var owner = OwnerKeys.Of(context);
        using var job = Job(context);
        var (pages, _) = await PagesAsync(context, request.Pages, files, documents, job.Token);
        if (pages == null)
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        var target = files.NewPath(owner);
        await PdfSaver.ExtractAsync(pages, target, null, null, job.Token);
        return await RegisterAsync(owner, target, request.Name, files, documents, job.Token);
    }

    private static async Task<IResult> BlankAsync(HttpContext context, BlankRequest request, FileStore files, OpenDocuments documents)
    {
        if (!(request.Width is >= 1 and <= 14400) || !(request.Height is >= 1 and <= 14400))
            return Error(ApiErrors.Invalid, StatusCodes.Status400BadRequest);
        var owner = OwnerKeys.Of(context);
        using var job = Job(context);
        var target = files.NewPath(owner);
        await File.WriteAllBytesAsync(target, BlankPdf.Create(new PageSizePt(request.Width, request.Height)), job.Token);
        return await RegisterAsync(owner, target, "blank.pdf", files, documents, job.Token);
    }

    private static async Task<IResult> RegisterAsync(string owner, string path, string name, FileStore files, OpenDocuments documents,
        CancellationToken token)
    {
        StoredFile file;
        try
        {
            file = files.Register(owner, path, name);
        }
        catch (QuotaExceededException)
        {
            return Error(ApiErrors.QuotaExceeded, StatusCodes.Status507InsufficientStorage);
        }
        return Results.Json(await documents.DescribeAsync(file, token), RemoteJson.Default.RemoteFileInfo);
    }
}
