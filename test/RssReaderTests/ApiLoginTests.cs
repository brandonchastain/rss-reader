using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using Server.Authentication;

namespace RssReaderTests;

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
        Assert.AreEqual(5, identity.Claims.Count(), "provider claims must not bloat the cookie (4 app claims + the email link)");
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
    public void SwaPrincipal_WithoutClaims_LinksByEmailHash()
    {
        var header = SwaHeader(new
        {
            identityProvider = "aad",
            userId = "abc",
            userDetails = "Person@Example.com",
            userRoles = new[] { "authenticated" },
        });

        var parsed = StaticWebAppsAuthenticationHandler.ParseClientPrincipal(header)!;
        Assert.AreEqual("abc", parsed.UserId);
        var link = StaticWebAppsAuthenticationHandler.LinkedProviderUserId(parsed)!;
        StringAssert.StartsWith(link, "microsoft-email:");
        Assert.AreEqual(UserIdentity.EmailLinkId("person@example.com"), link, "hash is case-insensitive and never the plaintext");
        Assert.IsFalse(link.Contains("example.com"));
    }

    [TestMethod]
    public void SwaPrincipal_NoEmailNoClaims_NoLink()
    {
        var header = SwaHeader(new { identityProvider = "aad", userId = "abc", userDetails = "x", userRoles = new[] { "authenticated" } });
        Assert.IsNull(StaticWebAppsAuthenticationHandler.LinkedProviderUserId(StaticWebAppsAuthenticationHandler.ParseClientPrincipal(header)!));
    }

    [TestMethod]
    public void UserResolver_EmailHashLink_ThenFirstDirectLogin_LinksObjectId()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("024d97", 7)); fake.Link(7, "024d97");
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);
        var emailLink = UserIdentity.EmailLinkId("person@example.com");

        // Migration window: SWA visit records the email hash as an email-link identity.
        var swa = new ClaimsIdentity("aad");
        swa.AddClaim(new Claim(ClaimTypes.NameIdentifier, "024d97"));
        swa.AddClaim(new Claim(UserIdentity.LinkedUserIdClaim, emailLink));
        Assert.AreEqual(7, resolver.ResolveUser(new ClaimsPrincipal(swa))!.Id);
        Assert.AreEqual(7, fake.Identities[(UserIdentity.ProviderEmailLink, emailLink.Substring(UserIdentity.EmailLinkPrefix.Length))]);

        // After cutover: direct Microsoft login carries the object id and the email hash.
        var oidc = new ClaimsIdentity("oidc");
        oidc.AddClaim(new Claim("oid", "obj-1"));
        oidc.AddClaim(new Claim("email", "PERSON@example.com"));
        var principal = new ClaimsPrincipal(oidc);
        AuthenticationSetup.NormalizeIdentity(principal, "microsoft");
        Assert.AreEqual(emailLink, principal.FindFirst(UserIdentity.EmailLinkClaim)!.Value);

        var user = resolver.ResolveOrCreateUser(principal);
        Assert.AreEqual(7, user.Id, "must land on the pre-migration row");
        Assert.AreEqual(7, fake.Identities[("microsoft", "obj-1")], "the real id is linked to the same row");
        Assert.AreEqual(0, fake.AddUserCallCount);

        // A later SWA visit changes nothing.
        resolver.ResolveUser(new ClaimsPrincipal(swa));
        Assert.AreEqual(3, fake.Identities.Count);

        // And the next direct login finds the row by the real id.
        Assert.AreEqual(7, resolver.ResolveUser(principal)!.Id);
    }

    [TestMethod]
    public void NormalizeIdentity_NoEmail_RejectsTheSignIn()
    {
        // Only openid + email are requested, so an account without an email
        // address yields no display name at all; refuse rather than store "".
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("sub", "pairwise"));
        identity.AddClaim(new Claim("oid", "obj-1"));
        var principal = new ClaimsPrincipal(identity);

        Assert.ThrowsException<AuthenticationSetup.NoEmailException>(
            () => AuthenticationSetup.NormalizeIdentity(principal, "microsoft"));
    }

    [TestMethod]
    public void NormalizeIdentity_GitHub_HasNoEmailLink()
    {
        var identity = new ClaimsIdentity("oauth");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "42"));
        identity.AddClaim(new Claim(ClaimTypes.Email, "person@example.com"));
        var principal = new ClaimsPrincipal(identity);
        AuthenticationSetup.NormalizeIdentity(principal, "github");
        Assert.IsNull(principal.FindFirst(UserIdentity.EmailLinkClaim));
    }

    [TestMethod]
    public void UserResolver_SwaLogin_RecordsLinkedId_ThenDirectLoginFindsSameRow()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("024d97a14bc74c58b9a83e88b919a92c", 7));
        fake.Link(7, "024d97a14bc74c58b9a83e88b919a92c");
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // Migration window: SWA principal with the linked id claim.
        var swa = new ClaimsIdentity("aad");
        swa.AddClaim(new Claim(ClaimTypes.NameIdentifier, "024d97a14bc74c58b9a83e88b919a92c"));
        swa.AddClaim(new Claim(UserIdentity.LinkedUserIdClaim, "microsoft:obj-1"));
        var viaSwa = resolver.ResolveUser(new ClaimsPrincipal(swa));
        Assert.IsNotNull(viaSwa);
        Assert.AreEqual(7, viaSwa.Id);
        Assert.AreEqual(7, fake.Identities[("microsoft", "obj-1")]);

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
    public void UserResolver_NewProviderLogin_CreatesRowWithProviderIdentity()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "github:4242"));

        var user = resolver.ResolveOrCreateUser(new ClaimsPrincipal(identity));

        Assert.AreEqual("github:4242", user.Username);
        Assert.AreEqual(user.Id, fake.Identities[("github", "4242")]);
    }

    [TestMethod]
    public void AdminPolicy_MatchesEitherStoredId()
    {
        var config = new RssAppConfig { AdminAadUserIds = "024d97a14bc74c58b9a83e88b919a92c" };
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "microsoft:obj-1"));
        var principal = new ClaimsPrincipal(identity);

        Assert.IsFalse(AdminPolicy.IsAdmin(config, principal));
        var row = new RssUser("x", 1) { Identities = new[] { "microsoft:024d97a14bc74c58b9a83e88b919a92c", "microsoft:obj-1" } };
        Assert.IsTrue(AdminPolicy.IsAdmin(config, principal, row), "the legacy SWA id is matched as a bare subject");
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
    [DataRow("\\\\evil.example.com")]
    [DataRow("/\\/evil.example.com")]
    [DataRow("https://rss.example.com@evil.example.com/")]
    [DataRow("https://evil.example.com\\@rss.example.com/")]
    [DataRow("https://rss.example.com%40evil.example.com/")]
    [DataRow("https://rss.example.com%2Eevil.example.com/")]
    [DataRow("https:evil.example.com")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,hi")]
    [DataRow("file:///etc/passwd")]
    [DataRow("http://localhost:4281/")]
    [DataRow("not a url")]
    public void ReturnUrl_Rejected_FallsBack(string returnUrl)
    {
        Assert.AreEqual("https://rss.example.com/", ReturnUrlValidator.Resolve(returnUrl, Origins));
    }

    [TestMethod]
    public void ReturnUrl_RelativeWithQueryAndFragment_IsKept()
    {
        Assert.AreEqual("https://rss.example.com/feeds?tag=a#top", ReturnUrlValidator.Resolve("/feeds?tag=a#top", Origins));
    }

    [TestMethod]
    public void AdminPolicy_IgnoresRequestControlledClaims()
    {
        // Only identities the server stored or derived count; a principal that
        // merely claims roles or ids cannot promote itself.
        var config = new RssAppConfig { AdminAadUserIds = "admin-subject" };
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        identity.AddClaim(new Claim("role", "admin"));
        identity.AddClaim(new Claim("isAdmin", "true"));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "github:someone"));
        var principal = new ClaimsPrincipal(identity);

        Assert.IsFalse(AdminPolicy.IsAdmin(config, principal));
        Assert.IsFalse(AdminPolicy.IsAdmin(config, principal, new RssUser("someone", 7) { Identities = new[] { "github:someone" } }));
    }

    [TestMethod]
    public void AdminPolicy_EmptyConfig_NobodyIsAdmin()
    {
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "microsoft:anything"));
        Assert.IsFalse(AdminPolicy.IsAdmin(new RssAppConfig { AdminAadUserIds = "" }, new ClaimsPrincipal(identity)));
    }

    // ---------- Microsoft: personal accounts only ----------

    [TestMethod]
    public void Microsoft_DefaultAuthority_IsConsumers()
    {
        Assert.IsTrue(MicrosoftAccounts.IsConsumersAuthority(MicrosoftAccounts.ConsumersAuthority));
        Assert.IsTrue(MicrosoftAccounts.IsConsumersAuthority("https://login.microsoftonline.com/consumers/v2.0/"));
        Assert.IsFalse(MicrosoftAccounts.IsConsumersAuthority("https://login.microsoftonline.com/common/v2.0"));
        Assert.IsFalse(MicrosoftAccounts.IsConsumersAuthority("https://login.microsoftonline.com/contoso.onmicrosoft.com/v2.0"));
    }

    [TestMethod]
    public void Microsoft_Issuer_OnlyTheConsumersTenantIsAccepted()
    {
        Assert.AreEqual(MicrosoftAccounts.ConsumersIssuer, MicrosoftAccounts.ValidateIssuer(MicrosoftAccounts.ConsumersIssuer, null, null));
        Assert.AreEqual(MicrosoftAccounts.ConsumersIssuer + "/", MicrosoftAccounts.ValidateIssuer(MicrosoftAccounts.ConsumersIssuer + "/", null, null));
    }

    [DataTestMethod]
    [DataRow("https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0")]   // a work tenant
    [DataRow("https://login.microsoftonline.com/common/v2.0")]
    [DataRow("https://login.microsoftonline.com/{tenantid}/v2.0")]
    [DataRow("https://sts.windows.net/9188040d-6c67-4c5b-b112-36a304b66dad/")]           // v1 issuer form
    [DataRow("https://evil.example.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0")]
    [DataRow("")]
    public void Microsoft_Issuer_OtherTenantsAreRejected(string issuer)
    {
        Assert.ThrowsException<Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException>(
            () => MicrosoftAccounts.ValidateIssuer(issuer, null, null));
    }

    [TestMethod]
    public void Microsoft_WorkAccountToken_IsRefusedEvenWithAValidShape()
    {
        var identity = new ClaimsIdentity("oidc");
        identity.AddClaim(new Claim("sub", "pairwise"));
        identity.AddClaim(new Claim("oid", "obj-1"));
        identity.AddClaim(new Claim("email", "person@contoso.com"));
        identity.AddClaim(new Claim("tid", "72f988bf-86f1-41af-91ab-2d7cd011db47"));
        Assert.IsFalse(MicrosoftAccounts.IsPersonalAccount(new ClaimsPrincipal(identity)));

        var personal = new ClaimsIdentity("oidc");
        personal.AddClaim(new Claim("tid", MicrosoftAccounts.ConsumersTenantId));
        Assert.IsTrue(MicrosoftAccounts.IsPersonalAccount(new ClaimsPrincipal(personal)));

        var noTenant = new ClaimsIdentity("oidc");
        noTenant.AddClaim(new Claim("oid", "obj-1"));
        Assert.IsFalse(MicrosoftAccounts.IsPersonalAccount(new ClaimsPrincipal(noTenant)), "a token without a tenant claim is not trusted");
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
    public void Login_IssuesAPersistentCookie_SoTheSessionOutlivesTheBrowserWindow()
    {
        var props = AuthEndpoints.LoginProperties("https://rss.example.com/");

        Assert.IsTrue(props.IsPersistent, "without IsPersistent the browser drops the cookie on close");
        Assert.AreEqual("https://rss.example.com/", props.RedirectUri);
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

[TestClass]
public sealed class AdminControllerLinkedIdTests
{
    // The admin list holds the pre-migration SWA id. After cutover the login id
    // is microsoft:<oid>, so the controller must consult the resolved row.
    [TestMethod]
    public async Task GetUsers_AdminByLinkedRow_IsAllowed()
    {
        var config = new RssAppConfig { AdminAadUserIds = "024d97a14bc74c58b9a83e88b919a92c" };
        var repo = new FakeUserRepository();
        repo.Users.Add(new RssUser("legacy", 7));
        repo.Link(7, "024d97a14bc74c58b9a83e88b919a92c");
        repo.Link(7, "microsoft:obj-1");
        var resolver = new UserResolver(repo, NullLogger<UserResolver>.Instance);
        var stats = new Moq.Mock<RssApp.Data.ISystemStatsRepository>().Object;
        var controller = new Server.Controllers.AdminController(
            stats, repo, config, resolver,
            new RssReader.Server.Services.SystemStatsCollector(stats, config, NullLogger<RssReader.Server.Services.SystemStatsCollector>.Instance));
        var identity = new ClaimsIdentity("cookie");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "microsoft:obj-1"));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        var result = controller.GetUsers();

        Assert.IsInstanceOfType(result, typeof(Microsoft.AspNetCore.Mvc.OkObjectResult));
        await Task.CompletedTask;
    }
}
