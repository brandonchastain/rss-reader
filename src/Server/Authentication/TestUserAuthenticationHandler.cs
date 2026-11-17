using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RssApp.ComponentServices;

namespace Server.Authentication;

/// <summary>
/// Local-development bypass (RssAppConfig:IsTestUserEnabled): every request is
/// the same fake signed-in user, so no identity provider is needed. Registered
/// only when that flag is on, and Program.cs refuses the flag in Production.
/// There is no sign-in step to create the account, so this does it.
/// </summary>
public class TestUserAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthenticationScheme = "TestUser";

    private readonly IUserResolver userResolver;

    public TestUserAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IUserResolver userResolver)
        : base(options, logger, encoder)
    {
        this.userResolver = userResolver;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Logger.LogWarning("TEST MODE ENABLED - creating fake authenticated user");
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "test-user-id"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
        var principal = new ClaimsPrincipal(identity);

        this.userResolver.ResolveOrCreateUser(principal);

        var ticket = new AuthenticationTicket(principal, AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
