#nullable enable
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Server.Authentication;

/// <summary>
/// Local-development bypass (RssAppConfig:IsTestUserEnabled): every request is
/// the same fake signed-in user, so no identity provider is needed. Registered
/// only when that flag is on, and Program.cs refuses the flag in Production.
/// </summary>
public class TestUserAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthenticationScheme = "TestUser";

    public TestUserAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Logger.LogWarning("TEST MODE ENABLED - creating fake authenticated user");
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user-id"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "testuser"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
