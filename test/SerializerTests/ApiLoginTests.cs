using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using Server.Authentication;

namespace SerializerTests;

/// <summary>
/// The API-side login: stable user ids per provider, the migration link from a
/// Static Web Apps principal, returnUrl validation and the /auth/me envelope.
/// </summary>
[TestClass]
public sealed class ApiLoginTests
{
    private static readonly string[] Origins = { "https://rss.example.com", "http://localhost:4280" };

    // ---------- user id mapping ----------

    [TestMethod]
    public void BuildUserId_IsProviderColonSubject_Lowercased()
    {
        Assert.AreEqual("github:12345", UserIdentity.BuildUserId("GitHub", " 12345 "));
    }

    [TestMethod]
    public void ResolveSubject_Microsoft_PrefersOidOverSub()
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("sub", "pairwise-sub"));
        identity.AddClaim(new Claim("oid", "0000-oid"));

        Assert.AreEqual("0000-oid", UserIdentity.ResolveSubject("microsoft", new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public void ResolveSubject_Google_UsesSub()
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("sub", "109876"));

        Assert.AreEqual("109876", UserIdentity.ResolveSubject("google", new ClaimsPrincipal(identity)));
    }

    [TestMethod]
    public void NormalizeIdentity_LeavesOnlyTheFourAppClaims()
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("sub", "pairwise"));
        identity.AddClaim(new Claim("oid", "obj-1"));
        identity.AddClaim(new Claim("email", "person@example.com"));
        identity.AddClaim(new Claim("tid", "tenant"));
        var principal = new ClaimsPrincipal(identity);

        AuthenticationSetup.NormalizeIdentity(principal, "microsoft");

        Assert.AreEqual("microsoft:obj-1", principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.AreEqual("person@example.com", principal.FindFirst(ClaimTypes.Name)!.Value);
        Assert.AreEqual("authenticated", principal.FindFirst(ClaimTypes.Role)!.Value);
        Assert.AreEqual("microsoft", principal.FindFirst(UserIdentity.IdentityProviderClaim)!.Value);
        Assert.AreEqual(4, identity.Claims.Count(), "provider claims must not bloat the cookie");
    }

    // ---------- migration link from the SWA principal ----------

    private static string SwaHeader(object principal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal)));

    [TestMethod]
    public void SwaPrincipal_WithEntraOid_YieldsMicrosoftLinkedId()
    {
        var header = SwaHeader(new
        {
            identityProvider = "aad",
            userId = "024d97a14bc74c58b9a83e88b919a92c",
            userDetails = "person@example.com",
            userRoles = new[] { "anonymous", "authenticated" },
            claims = new[]
            {
                new { typ = "name", val = "Person" },
                new { typ = "http://schemas.microsoft.com/identity/claims/objectidentifier", val = "11111111-2222-3333-4444-555555555555" },
            }
        });

        var parsed = StaticWebAppsAuthenticationHandler.ParseClientPrincipal(header)!;
        Assert.AreEqual("024d97a14bc74c58b9a83e88b919a92c", parsed.UserId);
        Assert.AreEqual("microsoft:11111111-2222-3333-4444-555555555555",
            StaticWebAppsAuthenticationHandler.LinkedProviderUserId(parsed));
    }

    [TestMethod]
    public void SwaPrincipal_WithoutClaims_StillParses_NoLink()
    {
        var header = SwaHeader(new
        {
            identityProvider = "aad",
            userId = "abc",
            userDetails = "x",
            userRoles = new[] { "authenticated" },
        });

        var parsed = StaticWebAppsAuthenticationHandler.ParseClientPrincipal(header)!;
        Assert.AreEqual("abc", parsed.UserId);
        Assert.IsNull(StaticWebAppsAuthenticationHandler.LinkedProviderUserId(parsed));
    }

    [TestMethod]
    public void UserResolver_SwaLogin_RecordsLinkedId_ThenDirectLoginFindsSameRow()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("024d97a14bc74c58b9a83e88b919a92c", 7));
        fake.AadUserIds[7] = "024d97a14bc74c58b9a83e88b919a92c";
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // Migration window: SWA principal with the linked id claim.
        var swa = new ClaimsIdentity("aad");
        swa.AddClaim(new Claim(ClaimTypes.NameIdentifier, "024d97a14bc74c58b9a83e88b919a92c"));
        swa.AddClaim(new Claim(UserIdentity.LinkedUserIdClaim, "microsoft:obj-1"));
        var viaSwa = resolver.ResolveUser(new ClaimsPrincipal(swa));
        Assert.IsNotNull(viaSwa);
        Assert.AreEqual(7, viaSwa.Id);
        Assert.AreEqual("microsoft:obj-1", fake.ProviderUserIds[7]);

        // After cutover: the API's own Microsoft login for the same person.
        var direct = new ClaimsIdentity("cookie");
        direct.AddClaim(new Claim(ClaimTypes.NameIdentifier, "microsoft:obj-1"));
        var viaApi = resolver.ResolveUser(new ClaimsPrincipal(direct));
        Assert.IsNotNull(viaApi);
        Assert.AreEqual(7, viaApi.Id, "direct login must land on the pre-migration row");

        // And it must not create a duplicate on register.
        Assert.AreEqual(7, resolver.ResolveOrCreateUser(new ClaimsPrincipal(direct)).Id);
        Assert.AreEqual(0, fake.AddUserCallCount);
    }

    [TestMethod]
    public void UserResolver_NewProviderLogin_CreatesRowWithProviderId()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "github:4242"));

        var user = resolver.ResolveOrCreateUser(new ClaimsPrincipal(identity));

        Assert.AreEqual("github:4242", user.Username);
        Assert.AreEqual("github:4242", fake.AadUserIds[user.Id]);
    }

    [TestMethod]
    public void AdminPolicy_MatchesEitherStoredId()
    {
        var config = new RssAppConfig { AdminAadUserIds = "024d97a14bc74c58b9a83e88b919a92c" };
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "microsoft:obj-1"));
        var principal = new ClaimsPrincipal(identity);

        Assert.IsFalse(AdminPolicy.IsAdmin(config, principal));
        var row = new RssUser("x", 1) { AadUserId = "024d97a14bc74c58b9a83e88b919a92c", ProviderUserId = "microsoft:obj-1" };
        Assert.IsTrue(AdminPolicy.IsAdmin(config, principal, row));
    }

    // ---------- returnUrl validation ----------

    [TestMethod]
    public void ReturnUrl_Empty_FallsBackToFirstOriginRoot()
    {
        Assert.AreEqual("https://rss.example.com/", ReturnUrlValidator.Resolve(null, Origins));
        Assert.AreEqual("https://rss.example.com/", ReturnUrlValidator.Resolve("   ", Origins));
    }

    [TestMethod]
    public void ReturnUrl_RelativePath_ResolvesAgainstFirstOrigin()
    {
        Assert.AreEqual("https://rss.example.com/timeline?x=1", ReturnUrlValidator.Resolve("/timeline?x=1", Origins));
    }

    [TestMethod]
    public void ReturnUrl_AbsoluteOnAllowedOrigin_IsKept()
    {
        Assert.AreEqual("http://localhost:4280/feeds", ReturnUrlValidator.Resolve("http://localhost:4280/feeds", Origins));
        Assert.AreEqual("https://rss.example.com/a", ReturnUrlValidator.Resolve("HTTPS://RSS.EXAMPLE.COM/a", Origins));
    }

    [DataTestMethod]
    [DataRow("https://evil.example.com/")]
    [DataRow("https://rss.example.com.evil.com/")]
    [DataRow("https://rss.example.com:8443/")]
    [DataRow("//evil.example.com/")]
    [DataRow("/\\evil.example.com")]
    [DataRow("javascript:alert(1)")]
    [DataRow("http://localhost:4281/")]
    [DataRow("not a url")]
    public void ReturnUrl_Rejected_FallsBack(string returnUrl)
    {
        Assert.AreEqual("https://rss.example.com/", ReturnUrlValidator.Resolve(returnUrl, Origins));
    }

    [TestMethod]
    public void ReturnUrl_NoOriginsConfigured_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(() => ReturnUrlValidator.Resolve("/", Array.Empty<string>()));
    }

    // ---------- /auth/me shape ----------

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void AuthMe_Anonymous_IsNullClientPrincipal()
    {
        var json = JsonSerializer.Serialize(ClientPrincipalResponse.From(new ClaimsPrincipal(new ClaimsIdentity())), Json);
        Assert.AreEqual("{\"clientPrincipal\":null}", json);
    }

    [TestMethod]
    public void AuthMe_Authenticated_MatchesStaticWebAppsEnvelope()
    {
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "github:4242"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "octocat"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "authenticated"));
        identity.AddClaim(new Claim(UserIdentity.IdentityProviderClaim, "github"));

        var json = JsonSerializer.Serialize(ClientPrincipalResponse.From(new ClaimsPrincipal(identity)), Json);
        using var doc = JsonDocument.Parse(json);
        var cp = doc.RootElement.GetProperty("clientPrincipal");

        Assert.AreEqual("github", cp.GetProperty("identityProvider").GetString());
        Assert.AreEqual("github:4242", cp.GetProperty("userId").GetString());
        Assert.AreEqual("octocat", cp.GetProperty("userDetails").GetString());
        CollectionAssert.AreEqual(new[] { "authenticated" },
            cp.GetProperty("userRoles").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [TestMethod]
    public void AuthMe_AlwaysIncludesAuthenticatedRole_AndDropsAnonymous()
    {
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "google:1"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "anonymous"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));

        var roles = ClientPrincipalResponse.From(new ClaimsPrincipal(identity)).ClientPrincipal!.UserRoles;
        CollectionAssert.AreEqual(new[] { "authenticated", "admin" }, roles.ToArray());
    }

    [TestMethod]
    public void Providers_ListsOnlyConfiguredKnownOnes()
    {
        var auth = new AuthConfig();
        auth.Providers["github"] = new AuthProviderConfig { ClientId = "id", ClientSecret = "s" };
        auth.Providers["google"] = new AuthProviderConfig { ClientId = "id" };
        auth.Providers["facebook"] = new AuthProviderConfig { ClientId = "id", ClientSecret = "s" };

        CollectionAssert.AreEqual(new[] { "github" }, AuthEndpoints.ConfiguredProviders(auth).ToArray());
    }
}
