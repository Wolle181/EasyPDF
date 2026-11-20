using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyPDF.Core.Imaging;
using EasyPDF.Core.Ocr;
using EasyPDF.Core.Pdfium;

namespace EasyPDF.Remote;

/// <summary>The server's error, with the code the browser translates (<see cref="ApiErrors"/>).</summary>
public sealed class RemoteApiException(string code, HttpStatusCode status) : Exception(code)
{
    public string Code { get; } = code;
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The HTTP calls of the web API (one instance per browser tab).</summary>
public sealed class RemoteApi
{
    private readonly HttpClient _http;

    /// <param name="http">Client with the page's address as base address.</param>
    public RemoteApi(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
    }

    public async Task<ServerConfig> GetConfigAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync("api/config", token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.ServerConfig, token).ConfigureAwait(false))!;
    }

    /// <summary>Uploads a PDF; <paramref name="progress"/> gets 0..1.</summary>
    public async Task<RemoteFileInfo> UploadAsync(Stream content, string fileName, long? length = null, IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        using var body = new ProgressContent(content, length, progress);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/files") { Content = body };
        request.Headers.Add(ApiHeaders.FileName, Uri.EscapeDataString(fileName));
        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo, token).ConfigureAwait(false))!;
    }

    public async Task<RenderedPage> RenderAsync(string id, int page, int width, int height, int rotation, CancellationToken token)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"api/files/{id}/pages/{page}?w={width}&h={height}&rot={rotation}");
        var bytes = await GetBytesAsync(url, token).ConfigureAwait(false);
        return PngCodec.Decode(bytes);
    }

    public async Task<RenderedPage> RenderAreaAsync(string id, int page, int pageWidth, int pageHeight, int rotation, int x, int y, int width,
        int height, CancellationToken token)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"api/files/{id}/pages/{page}/area?pw={pageWidth}&ph={pageHeight}&rot={rotation}&x={x}&y={y}&w={width}&h={height}");
        var bytes = await GetBytesAsync(url, token).ConfigureAwait(false);
        return PngCodec.Decode(bytes);
    }

    public async Task<IReadOnlyList<OcrWord>> ReadWordsAsync(string id, int page, CancellationToken token)
    {
        using var response = await _http.GetAsync($"api/files/{id}/pages/{page}/words", token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.IReadOnlyListOcrWord, token).ConfigureAwait(false))!;
    }

    public async Task<int> CountTextCharsAsync(string id, int page, CancellationToken token)
    {
        using var response = await _http.GetAsync($"api/files/{id}/pages/{page}/chars", token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(RemoteJson.Default.Int32, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PdfAttachment>> ListAttachmentsAsync(string id, CancellationToken token)
    {
        using var response = await _http.GetAsync($"api/files/{id}/attachments", token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.IReadOnlyListPdfAttachment, token).ConfigureAwait(false))!;
    }

    public Task<byte[]> ReadAttachmentAsync(string id, int index, CancellationToken token) =>
        GetBytesAsync($"api/files/{id}/attachments/{index}", token);

    /// <summary>The PDF file itself.</summary>
    public Task<byte[]> DownloadAsync(string id, CancellationToken token = default) => GetBytesAsync($"api/files/{id}/content", token);

    public async Task<RemoteFileInfo> BuildAsync(BuildRequest request, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("api/build", request, RemoteJson.Default.BuildRequest, token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo, token).ConfigureAwait(false))!;
    }

    public async Task<RemoteFileInfo> ExtractAsync(ExtractRequest request, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("api/extract", request, RemoteJson.Default.ExtractRequest, token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo, token).ConfigureAwait(false))!;
    }

    public async Task<RemoteFileInfo> CreateBlankAsync(PageSizePt size, CancellationToken token)
    {
        using var response = await _http.PostAsJsonAsync("api/blank", new BlankRequest(size.Width, size.Height), RemoteJson.Default.BlankRequest, token)
            .ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(RemoteJson.Default.RemoteFileInfo, token).ConfigureAwait(false))!;
    }

    /// <summary>Deletes the file on the server (best effort: errors are ignored, the server's retention removes it anyway).</summary>
    public async Task DeleteAsync(string id)
    {
        try
        {
            using var response = await _http.DeleteAsync($"api/files/{id}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        await EnsureAsync(response, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
            return;
        string code = response.StatusCode switch
        {
            HttpStatusCode.NotFound => ApiErrors.NotFound,
            HttpStatusCode.TooManyRequests => ApiErrors.TooManyRequests,
            HttpStatusCode.RequestEntityTooLarge => ApiErrors.TooLarge,
            _ => ApiErrors.Invalid,
        };
        try
        {
            var error = await response.Content.ReadFromJsonAsync(RemoteJson.Default.ApiError, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(error?.Code))
                code = error.Code;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
        }
        throw new RemoteApiException(code, response.StatusCode);
    }

    /// <summary>Request body that reports how much of the stream has been sent.</summary>
    private sealed class ProgressContent(Stream source, long? length, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[81920];
            long sent = 0;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                sent += read;
                if (length is > 0)
                    progress?.Report(Math.Min(1, (double)sent / length.Value));
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length ?? 0;
            return length != null;
        }
    }
}
