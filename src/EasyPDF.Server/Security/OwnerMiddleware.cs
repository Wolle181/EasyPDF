using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EasyPDF.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace EasyPDF.Server.Security;

/// <summary>
/// The anonymous sessions of mode <see cref="AuthenticationMode.None"/>: a random id in a cookie, known to the server
/// only while the session is in use. An unknown or expired id (server restart, idle for too long) gets a new session;
/// the files of an expired session are deleted with it, so an old cookie never reaches them again.
/// </summary>
public sealed class SessionStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public int Count => _lastSeen.Count;

    public string Create()
    {
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _lastSeen[id] = time.GetUtcNow();
        return id;
    }

    /// <summary>Whether <paramref name="id"/> is a live session (then its idle time starts again).</summary>
    public bool Touch(string id, TimeSpan idle)
    {
        var now = time.GetUtcNow();
        if (!_lastSeen.TryGetValue(id, out var seen))
            return false;
        if (now - seen > idle)
        {
            _lastSeen.TryRemove(id, out _);
            return false;
        }
        _lastSeen[id] = now;
        return true;
    }

    /// <summary>Removes the sessions idle for longer than <paramref name="idle"/>; returns their owner keys.</summary>
    public string[] RemoveExpired(TimeSpan idle)
    {
        var now = time.GetUtcNow();
        var expired = _lastSeen.Where(p => now - p.Value > idle).Select(p => p.Key).ToArray();
        foreach (var id in expired)
            _lastSeen.TryRemove(id, out _);
        return [.. expired.Select(OwnerKeys.ForSession)];
    }
}

/// <summary>Owner keys: who a file belongs to, without the session id or user id itself on disk.</summary>
public static class OwnerKeys
{
    public const string ItemKey = "Owner";

    public static string ForSession(string sessionId) => "s" + Hash(sessionId);

    public static string ForUser(string subject) => "u" + Hash(subject);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    public static string Of(HttpContext context) =>
        context.Items[ItemKey] as string ?? throw new InvalidOperationException("No owner for this request.");
}

/// <summary>
/// For every API request: rejects changing requests without the app's own header (CSRF: a foreign page cannot set it
/// without a CORS permission, which the server never gives) and determines the owner of the files: the signed-in user
/// (mode Oidc) or the anonymous session from the cookie (mode None, created on first use).
/// </summary>
public sealed class OwnerMiddleware(RequestDelegate next, IOptions<ServerOptions> options, SessionStore sessions)
{
    /// <summary>"__Host-": only over HTTPS, only for this host, path "/" (browsers enforce it).</summary>
    public const string CookieName = "__Host-session";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !context.Request.Headers.ContainsKey(ApiHeaders.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ApiError(ApiErrors.Invalid), RemoteJson.Default.ApiError);
            return;
        }

        var auth = options.Value.Authentication;
        if (auth.Mode == AuthenticationMode.Oidc)
        {
            var subject = context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name;
            if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(subject))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Items[OwnerKeys.ItemKey] = OwnerKeys.ForUser(subject);
        }
        else
        {
            var idle = TimeSpan.FromMinutes(Math.Max(1, auth.SessionIdleMinutes));
            var id = context.Request.Cookies[CookieName];
            if (string.IsNullOrEmpty(id) || !sessions.Touch(id, idle))
            {
                id = sessions.Create();
                context.Response.Cookies.Append(CookieName, id, new CookieOptions
                {
                    Secure = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    IsEssential = true,
                });
            }
            context.Items[OwnerKeys.ItemKey] = OwnerKeys.ForSession(id);
        }
        await next(context);
    }
}
