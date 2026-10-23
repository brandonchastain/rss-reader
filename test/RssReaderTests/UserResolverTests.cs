using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Contracts;
using RssApp.Data;
using Server.Authentication;

namespace RssReaderTests;

[TestClass]
public sealed class UserResolverTests
{
    private static ClaimsPrincipal CreatePrincipal(string? loginId = null, string? email = null)
    {
        var identity = new ClaimsIdentity("Test");
        if (loginId != null)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, loginId));
        if (email != null)
            identity.AddClaim(new Claim(ClaimTypes.Name, email));
        return new ClaimsPrincipal(identity);
    }

    // ---------- ResolveUser ----------

    [TestMethod]
    public void ResolveUser_FindsByLegacySwaId_FiledUnderMicrosoft()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("anon-guid", 1));
        fake.Link(1, "aad-123");
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(loginId: "aad-123"));

        Assert.IsNotNull(user);
        Assert.AreEqual(1, user.Id);
        Assert.IsTrue(fake.Identities.ContainsKey((UserIdentity.ProviderMicrosoft, "aad-123")));
    }

    [TestMethod]
    public void ResolveUser_FindsByProviderIdentity()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("github:4242", 5));
        fake.Link(5, "github:4242");
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(loginId: "github:4242"));

        Assert.IsNotNull(user);
        Assert.AreEqual(5, user.Id);
        CollectionAssert.Contains(user.Identities.ToList(), "github:4242");
    }

    [TestMethod]
    public void ResolveUser_ReturnsNull_WhenNeitherFound()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(loginId: "aad-ghost"));

        Assert.IsNull(user);
    }

    // ---------- ResolveOrCreateUser ----------

    [TestMethod]
    public void ResolveOrCreateUser_CreatesNewUser_WithLoginIdAsUsername_AndLinksIdentity()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveOrCreateUser(CreatePrincipal(loginId: "google:109876", email: "brand@new.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual("google:109876", user.Username, "New user should have the login id as username, not email.");
        Assert.AreEqual(1, fake.AddUserCallCount);
        Assert.AreEqual(user.Id, fake.Identities[("google", "109876")]);
    }

    [TestMethod]
    public void ResolveOrCreateUser_ReturnsExistingUser_WhenFoundByIdentity()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("some-guid", 3));
        fake.Link(3, "aad-already");
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveOrCreateUser(CreatePrincipal(loginId: "aad-already", email: "whatever@email.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual(3, user.Id);
        Assert.AreEqual(0, fake.AddUserCallCount, "Should not create a new user.");
    }

    // ---------- Edge cases ----------

    [TestMethod]
    public void ResolveUser_EmailOnly_NoClaims_ReturnsNull()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal());

        Assert.IsNull(user);
    }

    [TestMethod]
    public void ResolveUser_NoLoginId_ReturnsNull()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("test@test.com", 7));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // No login id claim — should return null (email is not used for lookup)
        var user = resolver.ResolveUser(CreatePrincipal(email: "test@test.com"));

        Assert.IsNull(user);
    }

    // ---------- Error handling ----------

    [TestMethod]
    public void ResolveOrCreateUser_ReturnsUser_WhenLinkIdentityThrows()
    {
        var fake = new FakeUserRepository();
        fake.ThrowOnLinkIdentity = true;
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // Should NOT throw — the new user is still returned
        var user = resolver.ResolveOrCreateUser(CreatePrincipal(loginId: "aad-brand-new", email: "new@user.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual("aad-brand-new", user.Username);
        Assert.AreEqual(1, fake.AddUserCallCount, "User should have been created.");
        Assert.AreEqual(1, fake.LinkIdentityCallCount, "LinkIdentity should have been attempted.");
        Assert.AreEqual(0, fake.Identities.Count, "No identity should have been linked (it threw).");
    }

    // ---------- Identity parsing ----------

    [TestMethod]
    public void ParseIdentity_SplitsKnownProviders_AndFilesLegacyIdsUnderMicrosoft()
    {
        Assert.AreEqual(("github", "4242"), UserIdentity.ParseIdentity("github:4242"));
        Assert.AreEqual(("microsoft", "obj-1"), UserIdentity.ParseIdentity("microsoft:obj-1"));
        Assert.AreEqual(("email-link", "abc123"), UserIdentity.ParseIdentity("microsoft-email:abc123"));
        Assert.AreEqual(("microsoft", "024d97a14bc74c58b9a83e88b919a92c"), UserIdentity.ParseIdentity("024d97a14bc74c58b9a83e88b919a92c"));
        Assert.AreEqual(("microsoft", "urn:weird"), UserIdentity.ParseIdentity("urn:weird"), "unknown prefixes are not providers");
        Assert.AreEqual("microsoft-email:abc123", UserIdentity.FormatIdentity("email-link", "abc123"));
    }
}
