using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EasyPDF.Server.Security;

/// <summary>
/// Sets the security headers of every response (taken over from the hardening of the other web apps on the same host):
/// <c>X-Content-Type-Options</c>, <c>Referrer-Policy</c>, <c>Permissions-Policy</c> and a <c>Content-Security-Policy</c>.
///
/// They come from the app, not from the reverse proxy: on a Synology the proxy's "custom headers" only apply to the
/// request to the app, not to the response. HSTS and <c>X-Frame-Options</c> are set by the proxy (DSM) already.
///
/// The CSP allows only resources from the own host. The browser program is .NET WebAssembly, which needs
/// <c>'wasm-unsafe-eval'</c> (compiling WebAssembly, not JavaScript <c>eval</c>); inline scripts are not allowed at all.
/// Inline styles stay allowed (the loading screen). The headers are set in <c>OnStarting</c>, so they survive
/// <c>UseExceptionHandler</c> clearing the headers; headers already set (proxy, later middleware) are not overwritten.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    internal const string ContentTypeOptions = "nosniff";
    internal const string ReferrerPolicy = "strict-origin-when-cross-origin";
    internal const string PermissionsPolicy = "camera=(), microphone=(), geolocation=()";

    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "worker-src 'self' blob:; " +
        "media-src 'self' blob:; " +
        "object-src 'none'; " +
        "frame-ancestors 'self'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.TryAdd("X-Content-Type-Options", ContentTypeOptions);
            headers.TryAdd("Referrer-Policy", ReferrerPolicy);
            headers.TryAdd("Permissions-Policy", PermissionsPolicy);
            headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
            return Task.CompletedTask;
        });
        return next(context);
    }
}
