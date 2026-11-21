using System.IO;
using System.Linq;
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
/// Serves the precompressed copy (<c>file.br</c>, written by the WebAssembly publish) when the browser accepts Brotli:
/// the program is several megabytes, compressed about a quarter of that, without compressing on every request.
/// </summary>
public sealed class PrecompressedFilesMiddleware(RequestDelegate next, IFileProvider files)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (HttpMethods.IsGet(context.Request.Method) && !string.IsNullOrEmpty(path) && !path.StartsWith("/api")
            && context.Request.Headers.AcceptEncoding.Any(v => v != null && v.Contains("br"))
            && StaticContentTypes.Provider.TryGetContentType(path, out var contentType))
        {
            var compressed = files.GetFileInfo(path + ".br");
            if (compressed.Exists && !compressed.IsDirectory)
            {
                context.Response.Headers.ContentEncoding = "br";
                context.Response.Headers.Append(HeaderNames.Vary, "Accept-Encoding");
                context.Response.ContentType = contentType;
                context.Response.ContentLength = compressed.Length;
                if (path.Contains("/_framework/"))
                    context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(compressed);
                return;
            }
        }
        await next(context);
    }
}
