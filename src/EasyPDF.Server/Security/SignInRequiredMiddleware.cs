using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace EasyPDF.Server.Security;

/// <summary>
/// Mode Oidc: everything except the health check needs a signed-in user who meets the policy (group/role). The page
/// itself sends the browser to the identity provider; API calls get 401/403.
/// </summary>
public sealed class SignInRequiredMiddleware(RequestDelegate next, IAuthorizationService authorization)
{
    public const string Policy = "SignedIn";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }
        var result = await authorization.AuthorizeAsync(context.User, Policy);
        if (result.Succeeded)
        {
            await next(context);
            return;
        }
        if (context.User.Identity?.IsAuthenticated == true)
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else
            await context.ChallengeAsync();
    }
}
