#nullable enable
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Server.Authentication;

public class StaticWebAppsAuthenticationOptions : AuthenticationSchemeOptions
{
    public bool IsTestUserEnabled { get; set; }
}

public class StaticWebAppsAuthenticationHandler : AuthenticationHandler<StaticWebAppsAuthenticationOptions>
{
    public const string AuthenticationScheme = "StaticWebApps";

    public StaticWebAppsAuthenticationHandler(
        IOptionsMonitor<StaticWebAppsAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        try
        {
            Logger.LogInformation("AUTH HANDLER INVOKED - IsTestUserEnabled: {IsTestMode}", Options.IsTestUserEnabled);
            
            // Log all headers for debugging
            var xmsHeaders = Request.Headers.Where(h => h.Key.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase)).ToList();
            Logger.LogInformation("Found {Count} X-MS-* headers: {Headers}", xmsHeaders.Count, string.Join(", ", xmsHeaders.Select(h => h.Key)));

            // In test mode, create a fake authenticated user to bypass auth
            if (Options.IsTestUserEnabled)
            {
                Logger.LogWarning("TEST MODE ENABLED - creating fake authenticated user");
                var testIdentity = new ClaimsIdentity("TestAuth");
                testIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user-id"));
                testIdentity.AddClaim(new Claim(ClaimTypes.Name, "testuser"));
                testIdentity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
                
                var testPrincipal = new ClaimsPrincipal(testIdentity);
                var testTicket = new AuthenticationTicket(testPrincipal, AuthenticationScheme);
                return Task.FromResult(AuthenticateResult.Success(testTicket));
            }

            // Check for the X-MS-CLIENT-PRINCIPAL header
            if (!Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var headerValue))
            {
                Logger.LogWarning("X-MS-CLIENT-PRINCIPAL HEADER NOT FOUND - Authentication will fail");
                // No authentication header present
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Logger.LogInformation("X-MS-CLIENT-PRINCIPAL header FOUND - attempting to parse");
            var principal = ParseClientPrincipal(headerValue.ToString());
            if (principal == null)
            {
                Logger.LogWarning("Failed to parse X-MS-CLIENT-PRINCIPAL header");
                return Task.FromResult(AuthenticateResult.Fail("Invalid client principal"));
            }

            Logger.LogInformation("Successfully parsed principal - User: {UserDetails}, Provider: {Provider}", 
                principal.UserDetails, principal.IdentityProvider);

            var identity = new ClaimsIdentity(principal.IdentityProvider, ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, principal.UserId));
            identity.AddClaim(new Claim(ClaimTypes.Name, principal.UserDetails));
            
            foreach (var role in principal.UserRoles)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            var claimsPrincipal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(claimsPrincipal, AuthenticationScheme);

            Logger.LogInformation("AUTHENTICATION SUCCESS for user: {UserDetails}", principal.UserDetails);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error authenticating request");
            return Task.FromResult(AuthenticateResult.Fail("Error parsing authentication header"));
        }
    }

    private ClientPrincipal? ParseClientPrincipal(string headerValue)
    {
        try
        {
            var data = Convert.FromBase64String(headerValue);
            var json = Encoding.UTF8.GetString(data);
            var clientPrincipal = JsonSerializer.Deserialize<ClientPrincipal>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return clientPrincipal;
        }
        catch
        {
            return null;
        }
    }

    private class ClientPrincipal
    {
        public string IdentityProvider { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserDetails { get; set; } = string.Empty;
        public IEnumerable<string> UserRoles { get; set; } = Array.Empty<string>();
    }
}
