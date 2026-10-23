#nullable enable
using Microsoft.AspNetCore.Http;

namespace RssApp.Config;

/// <summary>
/// Login configuration for the API (RssAppConfig:Auth). The API is the relying
/// party: each configured provider is an OpenID Connect / OAuth app registration
/// whose client id and secret live here, and the API issues its own session
/// cookie after the provider has verified the user.
/// </summary>
public class AuthConfig
{
    /// <summary>Provider key (google, microsoft, github) -> app registration.</summary>
    public Dictionary<string, AuthProviderConfig> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the session cookie the API issues.</summary>
    public string CookieName { get; set; } = ".rss.session";

    /// <summary>
    /// Cookie domain. Leave empty to scope the cookie to the API host, which is
    /// what the cross-origin frontend needs: the browser attaches it to
    /// credentialed fetches to the API origin. Set it only when the frontend
    /// must read the cookie too (it never does today).
    /// </summary>
    public string CookieDomain { get; set; } = string.Empty;

    /// <summary>
    /// SameSite for the session cookie. The frontend and the API are different
    /// origins, so cross-site fetches need None (which also forces Secure).
    /// Lax works when both run on localhost (same site) over plain http.
    /// </summary>
    public SameSiteMode CookieSameSite { get; set; } = SameSiteMode.None;

    /// <summary>How long a session lives without activity (sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Frontend origins (scheme://host[:port]) allowed to call the API with
    /// credentials and to be the target of a login/logout returnUrl. The first
    /// one is the default return target.
    /// </summary>
    public string[] AllowedFrontendOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Directory the session-cookie encryption keys persist in. Without it the
    /// keys live in the container, so every deploy or restart would sign
    /// everyone out. In Azure this is a folder on the /data mount.
    /// </summary>
    public string DataProtectionKeysPath { get; set; } = string.Empty;

    /// <summary>
    /// Migration-window flag: keep accepting the X-Gateway-Key / X-User-Id
    /// headers the Static Web Apps Functions proxy sends. Remove once the
    /// frontend logs in through the API directly.
    /// </summary>
    public bool EnableStaticWebAppsHeaders { get; set; } = true;

    /// <summary>
    /// Login and logout attempts allowed per client address per minute. Sign-in
    /// is delegated to the providers, so this only bounds abuse of the
    /// redirect endpoints themselves.
    /// </summary>
    public int LoginRateLimitPerMinute { get; set; } = 30;

    /// <summary>Feed additions (and OPML imports) allowed per user per minute.</summary>
    public int AddFeedRateLimitPerMinute { get; set; } = 30;
}

public class AuthProviderConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// OpenID Connect authority. Defaults per provider:
    /// google -> https://accounts.google.com,
    /// microsoft -> https://login.microsoftonline.com/consumers/v2.0, which
    /// admits personal Microsoft accounts only (the issuer and tenant claim
    /// are then matched exactly; see MicrosoftAccounts). Point it at a tenant
    /// to run the app for one organisation instead. GitHub is plain OAuth 2
    /// and ignores this.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
