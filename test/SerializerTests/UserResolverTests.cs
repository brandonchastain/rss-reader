using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RssApp.ComponentServices;
using RssApp.Contracts;
using RssApp.Data;

namespace SerializerTests;

[TestClass]
public sealed class UserResolverTests
{
    private static ClaimsPrincipal CreatePrincipal(string? aadId = null, string? email = null)
    {
        var identity = new ClaimsIdentity("Test");
        if (aadId != null)
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, aadId));
        if (email != null)
            identity.AddClaim(new Claim(ClaimTypes.Name, email));
        return new ClaimsPrincipal(identity);
    }

    // ---------- ResolveUser ----------

    [TestMethod]
    public void ResolveUser_FindsByAadId()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("anon-guid", 1));
        fake.AadUserIds[1] = "aad-123";
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(aadId: "aad-123"));

        Assert.IsNotNull(user);
        Assert.AreEqual(1, user.Id);
    }

    [TestMethod]
    public void ResolveUser_ReturnsNull_WhenNeitherFound()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(aadId: "aad-ghost"));

        Assert.IsNull(user);
    }

    // ---------- ResolveOrCreateUser ----------

    [TestMethod]
    public void ResolveOrCreateUser_CreatesNewUser_WithAadGuidAsUsername()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveOrCreateUser(CreatePrincipal(aadId: "aad-brand-new", email: "brand@new.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual("aad-brand-new", user.Username, "New user should have AAD GUID as username, not email.");
        Assert.AreEqual(1, fake.AddUserCallCount);
        Assert.AreEqual("aad-brand-new", fake.AadUserIds[user.Id]);
    }

    [TestMethod]
    public void ResolveOrCreateUser_ReturnsExistingUser_WhenFoundByAadId()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("some-guid", 3));
        fake.AadUserIds[3] = "aad-already";
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveOrCreateUser(CreatePrincipal(aadId: "aad-already", email: "whatever@email.com"));

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
    public void ResolveUser_NoAadId_ReturnsNull()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("test@test.com", 7));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // No AAD ID claim — should return null (email is not used for lookup)
        var user = resolver.ResolveUser(CreatePrincipal(email: "test@test.com"));

        Assert.IsNull(user);
    }

    // ---------- Error handling ----------

    [TestMethod]
    public void ResolveOrCreateUser_ReturnsUser_WhenSetAadUserIdThrows()
    {
        var fake = new FakeUserRepository();
        fake.ThrowOnSetAadUserId = true;
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // Should NOT throw — the new user is still returned
        var user = resolver.ResolveOrCreateUser(CreatePrincipal(aadId: "aad-brand-new", email: "new@user.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual("aad-brand-new", user.Username);
        Assert.AreEqual(1, fake.AddUserCallCount, "User should have been created.");
        Assert.AreEqual(1, fake.SetAadUserIdCallCount, "SetAadUserId should have been attempted.");
        Assert.IsFalse(fake.AadUserIds.ContainsKey(user.Id), "AadUserId should NOT have been set (it threw).");
    }
}
