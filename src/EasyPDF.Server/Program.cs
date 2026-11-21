using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EasyPDF.Remote;
using EasyPDF.Server.Security;
using EasyPDF.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyPDF.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Health check of the container (the image has no shell or curl): asks the running server and exits 0 or 1
        if (args.Contains("--healthcheck"))
            return await HealthCheckAsync();

        var app = Build(WebApplication.CreateBuilder(args));
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> HealthCheckAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    /// <summary>Configures services and the request pipeline (also used by the tests with their own configuration).</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
        var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // Uploads, and page lists with placed pictures (decoded pixels): up to the upload limit
            kestrel.Limits.MaxRequestBodySize = options.Storage.MaxUploadBytes + 1024 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, RemoteJson.Default));
        builder.Services.TryAddSingleton(TimeProvider.System); // tests put in their own clock first
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<FileStore>();
        builder.Services.AddSingleton<OpenDocuments>();
        builder.Services.AddSingleton<OcrService>();
        builder.Services.AddSingleton<CleanupService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CleanupService>());

        // Keys for the cookies (sign-in, OIDC correlation) survive restarts when the folder is writable
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("web");
        if (TryCreateFolder(options.Storage.KeysFolder))
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(options.Storage.KeysFolder));

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, token) =>
                await context.HttpContext.Response.WriteAsJsonAsync(new ApiError(ApiErrors.TooManyRequests), RemoteJson.Default.ApiError, cancellationToken: token);
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api")
                    ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(options.RateLimits.RequestsPerMinute))
                    : RateLimitPartition.GetNoLimiter("static"));
            limiter.AddPolicy(Api.UploadPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(context), _ => Window(options.RateLimits.UploadsPerMinute)));
        });

        if (options.Authentication.Mode == AuthenticationMode.Oidc)
            AddSingleSignOn(builder.Services, options.Authentication);

        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

        // First: the client address and scheme from the trusted reverse proxy (rate limits, secure cookies, redirects)
        var forwarded = ForwardedHeadersSetup.Create(options.ReverseProxy, RunningInContainer, ForwardedHeadersSetup.ReadRouteTable,
            out var trusted);
        logger.LogInformation("Reverse proxy: X-Forwarded-* accepted from loopback{Additional}",
            trusted.Count > 0 ? ", " + string.Join(", ", trusted) : "");
        logger.LogInformation("Authentication: {Mode}; files kept {Idle} min after last use, at most {Age} h", options.Authentication.Mode,
            options.Storage.IdleTimeoutMinutes, options.Storage.MaxAgeHours);
        app.UseForwardedHeaders(forwarded);
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage();
        else
        {
            app.UseExceptionHandler(error => error.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return context.Response.WriteAsJsonAsync(new ApiError(ApiErrors.Invalid), RemoteJson.Default.ApiError);
            }));
            app.UseHsts();
        }

        app.MapGet("/health", () => Results.Text("ok"));
        app.UseRateLimiter();
        if (options.Authentication.Mode == AuthenticationMode.Oidc)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<SignInRequiredMiddleware>();
        }
        app.UseMiddleware<OwnerMiddleware>();
        Api.Map(app);

        // The browser program (published .NET WebAssembly); precompressed files (.br) where the browser accepts them
        var webRoot = string.IsNullOrEmpty(options.WebRoot) ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : options.WebRoot;
        if (Directory.Exists(webRoot))
        {
            var files = new PhysicalFileProvider(webRoot);
            app.UseMiddleware<PrecompressedFilesMiddleware>(files);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = StaticContentTypes.Provider });
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
            });
        }
        else
            logger.LogWarning("No browser program at {Folder}: only the API is served", webRoot);
        return app;
    }

    private static bool RunningInContainer => Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    private static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = Math.Max(1, permits),
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    };

    private static bool TryCreateFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddSingleSignOn(IServiceCollection services, AuthenticationOptions auth)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "__Host-signin";
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax; // the identity provider returns with a cross-site redirect
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
                cookie.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(oidc =>
            {
                oidc.Authority = auth.Authority;
                oidc.ClientId = auth.ClientId;
                oidc.ClientSecret = auth.ClientSecret;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.MapInboundClaims = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                oidc.TokenValidationParameters.NameClaimType = "preferred_username";
                oidc.TokenValidationParameters.RoleClaimType = "roles";
            });
        services.AddAuthorizationBuilder().AddPolicy(SignInRequiredMiddleware.Policy, policy =>
        {
            policy.RequireAuthenticatedUser();
            if (!string.IsNullOrEmpty(auth.RequiredGroup))
                policy.RequireClaim("groups", auth.RequiredGroup);
            if (!string.IsNullOrEmpty(auth.RequiredRole))
                policy.RequireRole(auth.RequiredRole);
        });
    }
}
