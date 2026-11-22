using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace EasyPDF.Server;

/// <summary>Content types of the files of a .NET WebAssembly program.</summary>
public static class StaticContentTypes
{
    public static FileExtensionContentTypeProvider Provider { get; } = Create();

    private static FileExtensionContentTypeProvider Create()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".dat"] = "application/octet-stream";
        provider.Mappings[".blat"] = "application/octet-stream";
        provider.Mappings[".dll"] = "application/octet-stream";
        provider.Mappings[".pdb"] = "application/octet-stream";
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        provider.Mappings[".mp4"] = "video/mp4";
        return provider;
    }
}

/// <summary>
/// How long the browser may keep the program's files. The WebAssembly publish names every file of <c>_framework/</c> after
/// a hash of its content (<c>System.Text.Json.aucta6todm.wasm</c>), so a changed file (a new .NET version, a change of
/// the app) gets a new name and is downloaded again: those are kept for a month without asking (<c>immutable</c>). All
/// other files (<c>index.html</c>, <c>dotnet.js</c> with the list of those names, <c>main.js</c>, styles, videos) keep
/// their names across versions: the browser asks every time and gets "not modified" (304, by ETag) while they are
/// unchanged. <c>main.js</c> turns off the runtime's own <c>no-cache</c> fetching, which would ask for each file anyway.
/// </summary>
public static partial class StaticCaching
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public static string HashedFile => $"public, max-age={(int)MaxAge.TotalSeconds}, immutable";

    public const string Revalidate = "no-cache";

    /// <summary>A file name with the publish's content hash: <c>name.&lt;10 characters a-z0-9&gt;.ext</c>.</summary>
    [GeneratedRegex(@"\.[0-9a-z]{10}\.[a-z]+$")]
    private static partial Regex Hashed();

    public static bool IsHashed(string path) =>
        path.StartsWith("/_framework/", StringComparison.Ordinal) && Hashed().IsMatch(Path.GetFileName(path));

    public static string CacheControlFor(string path) => IsHashed(path) ? HashedFile : Revalidate;

    /// <summary>
    /// Sets the ETag of <paramref name="file"/> (and when it changed) and answers "not modified" (304) if the browser has
    /// that version already: true if the response is done.
    /// </summary>
    public static bool AnswerIfNotModified(HttpContext context, IFileInfo file, string kind)
    {
        var etag = new EntityTagHeaderValue($"\"{kind}{file.LastModified.ToUnixTimeSeconds():x}{file.Length:x}\"");
        context.Response.Headers.ETag = etag.ToString();
        context.Response.Headers.LastModified = file.LastModified.ToString("R");
        if (context.Request.GetTypedHeaders().IfNoneMatch is not { Count: > 0 } known
            || !known.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(etag, useStrongComparison: false)))
            return false;
        context.Response.StatusCode = StatusCodes.Status304NotModified;
        return true;
    }
}

/// <summary>
/// Serves the precompressed copy (<c>file.br</c>, written by the WebAssembly publish) when the browser accepts Brotli:
/// the program is several megabytes, compressed about a quarter of that, without compressing on every request. With an
/// ETag (answers "not modified" when the browser still has it) and the caching of <see cref="StaticCaching"/>.
/// </summary>
public sealed class PrecompressedFilesMiddleware(RequestDelegate next, IFileProvider files)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) && !string.IsNullOrEmpty(path)
            && !path.StartsWith("/api")
            && context.Request.Headers.AcceptEncoding.Any(v => v != null && v.Contains("br"))
            && StaticContentTypes.Provider.TryGetContentType(path, out var contentType))
        {
            var compressed = files.GetFileInfo(path + ".br");
            if (compressed.Exists && !compressed.IsDirectory)
            {
                var headers = context.Response.Headers;
                headers.Append(HeaderNames.Vary, "Accept-Encoding");
                headers.CacheControl = StaticCaching.CacheControlFor(path);
                if (StaticCaching.AnswerIfNotModified(context, compressed, "br"))
                    return;
                headers.ContentEncoding = "br";
                context.Response.ContentType = contentType;
                context.Response.ContentLength = compressed.Length;
                if (HttpMethods.IsGet(context.Request.Method))
                    await context.Response.SendFileAsync(compressed);
                return;
            }
        }
        await next(context);
    }
}
