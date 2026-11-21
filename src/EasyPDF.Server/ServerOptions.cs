using System;
using System.Collections.Generic;

namespace EasyPDF.Server;

/// <summary>Configuration section "Web" (appsettings.json, appsettings.Production.json in the container, environment variables Web__…).</summary>
public sealed class ServerOptions
{
    public const string Section = "Web";

    public AuthenticationOptions Authentication { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public RateLimitOptions RateLimits { get; set; } = new();

    public ReverseProxyOptions ReverseProxy { get; set; } = new();

    /// <summary>Folder with the browser program (index.html, _framework); empty = wwwroot next to the server.</summary>
    public string WebRoot { get; set; } = "";
}

public enum AuthenticationMode
{
    /// <summary>No sign-in (private use): each browser gets an anonymous session, files belong to it.</summary>
    None,

    /// <summary>Single sign-on with OpenID Connect (Keycloak, Entra ID, …): files belong to the signed-in user.</summary>
    Oidc,
}

public sealed class AuthenticationOptions
{
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.None;

    /// <summary>Address of the identity provider, e.g. https://sso.example.com/realms/company or https://login.microsoftonline.com/&lt;tenant&gt;/v2.0.</summary>
    public string Authority { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    /// <summary>Optional: only users with this group in the "groups" claim may use the app.</summary>
    public string RequiredGroup { get; set; } = "";

    /// <summary>Optional: only users with this role may use the app.</summary>
    public string RequiredRole { get; set; } = "";

    /// <summary>Lifetime of an anonymous session without requests (mode None); its files are deleted with it.</summary>
    public int SessionIdleMinutes { get; set; } = 30;
}

public sealed class StorageOptions
{
    /// <summary>Folder for uploaded and written PDFs (temporary; not worth a backup).</summary>
    public string Folder { get; set; } = "/data/files";

    /// <summary>Folder for the data protection keys (cookies stay valid across restarts).</summary>
    public string KeysFolder { get; set; } = "/data/keys";

    /// <summary>A file is deleted this long after its last use.</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>A file is deleted at the latest this long after it came to the server.</summary>
    public int MaxAgeHours { get; set; } = 8;

    public int MaxUploadMegabytes { get; set; } = 200;

    /// <summary>All files of one session or user together.</summary>
    public int MaxOwnerMegabytes { get; set; } = 1000;

    /// <summary>All files on the server together.</summary>
    public int MaxTotalMegabytes { get; set; } = 10000;

    public int CleanupIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Stamps and templates all users get (read-only): PNG files in <c>stamps/</c> (one level of subfolders, each a section
    /// of the gallery) and text files in <c>templates/</c>. Empty or missing: only the sample stamps.
    /// </summary>
    public string SharedFolder { get; set; } = "/data/shared";

    /// <summary>
    /// Desktop packages offered for download (Help: the download button in the header): files named like the installer
    /// scripts name them (<c>&lt;App&gt;-&lt;version&gt;-Windows-Installer.msi</c> …); the newest of each kind is listed.
    /// Empty or missing: no downloads (the button is hidden).
    /// </summary>
    public string DownloadsFolder { get; set; } = "/data/downloads";

    public long MaxUploadBytes => MaxUploadMegabytes * 1024L * 1024L;
}

public sealed class RateLimitOptions
{
    /// <summary>Requests per client address and minute (all API calls; a page view needs one per visible page).</summary>
    public int RequestsPerMinute { get; set; } = 1200;

    /// <summary>Uploads and written files (build, extract, blank page) per client address and minute.</summary>
    public int UploadsPerMinute { get; set; } = 30;

    /// <summary>Largest rendered page in pixels (width × height).</summary>
    public int MaxRenderPixels { get; set; } = 40_000_000;
}

public sealed class ReverseProxyOptions
{
    /// <summary>In a container: trust X-Forwarded-* from the container's gateway (where the host's reverse proxy comes from).</summary>
    public bool TrustContainerGateway { get; set; } = true;

    /// <summary>Further trusted proxies: IP addresses or networks in CIDR notation.</summary>
    public List<string> TrustedProxies { get; set; } = [];
}
