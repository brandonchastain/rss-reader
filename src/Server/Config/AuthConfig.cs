#nullable enable
using Microsoft.AspNetCore.Http;

namespace RssApp.Config;

/// <summary>Sign-in settings (the RssAppConfig:Auth section): providers, the session cookie, and the allowed frontend.</summary>
public class AuthConfig
{
    /// <summary>Sign-in providers by key (google, microsoft, github); a provider without both id and secret is disabled.</summary>
    public Dictionary<string, AuthProviderConfig> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Frontend origins (scheme://host[:port]) allowed to call the API with credentials and to receive login redirects; the first is the default.</summary>
    public string[] AllowedFrontendOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Name of the session cookie.</summary>
    public string CookieName { get; set; } = ".rss.session";

    /// <summary>Cookie domain; empty scopes the cookie to the API host.</summary>
    public string CookieDomain { get; set; } = string.Empty;

    /// <summary>SameSite mode of the session cookie; None is needed when the frontend is on another site, Lax works on localhost.</summary>
    public SameSiteMode CookieSameSite { get; set; } = SameSiteMode.None;

    /// <summary>Idle time before a session expires; activity extends it.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Directory holding the keys that encrypt session cookies; empty keeps them in the container, so restarts sign everyone out.</summary>
    public string DataProtectionKeysPath { get; set; } = string.Empty;

    /// <summary>Login and logout requests allowed per client address per minute.</summary>
    public int LoginRateLimitPerMinute { get; set; } = 30;

    /// <summary>Feed additions and OPML imports allowed per user per minute.</summary>
    public int AddFeedRateLimitPerMinute { get; set; } = 30;

    /// <summary>Legacy: still accept the X-Gateway-Key and X-User-Id headers of the retired Static Web Apps proxy.</summary>
    public bool EnableStaticWebAppsHeaders { get; set; } = true;
}

public class AuthProviderConfig
{
    /// <summary>OAuth client id of the app registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OAuth client secret of the app registration.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OpenID Connect authority; defaults are google https://accounts.google.com and microsoft https://login.microsoftonline.com/consumers/v2.0 (personal accounts only); ignored for github.</summary>
    public string Authority { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
