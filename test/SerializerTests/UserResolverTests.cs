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
    public void ResolveUser_FallsBackToEmail_WhenAadIdNotFound()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice@example.com", 1));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(aadId: "aad-new", email: "alice@example.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual(1, user.Id);
        // Should have lazy-migrated the AadUserId
        Assert.AreEqual(1, fake.SetAadUserIdCallCount);
        Assert.AreEqual("aad-new", fake.AadUserIds[1]);
    }

    [TestMethod]
    public void ResolveUser_ReturnsNull_WhenNeitherFound()
    {
        var fake = new FakeUserRepository();
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveUser(CreatePrincipal(aadId: "aad-ghost", email: "ghost@example.com"));

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
    public void ResolveOrCreateUser_ReturnsExistingUser_WhenFoundByEmail()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("existing@email.com", 5));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        var user = resolver.ResolveOrCreateUser(CreatePrincipal(aadId: "aad-migrate", email: "existing@email.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual(5, user.Id);
        Assert.AreEqual(0, fake.AddUserCallCount, "Should not create a new user.");
        Assert.AreEqual("aad-migrate", fake.AadUserIds[5], "Should have migrated AadUserId.");
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
    public void ResolveUser_NoAadId_EmailLookupOnly()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("test@test.com", 7));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // No AAD ID claim, only email
        var user = resolver.ResolveUser(CreatePrincipal(email: "test@test.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual(7, user.Id);
        // No migration since no AAD ID
        Assert.AreEqual(0, fake.SetAadUserIdCallCount);
    }

    // ---------- Error handling ----------

    [TestMethod]
    public void ResolveUser_ReturnsUser_WhenSetAadUserIdThrows()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice@example.com", 1));
        fake.ThrowOnSetAadUserId = true;
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // Should NOT throw — the user is still returned despite the migration failure
        var user = resolver.ResolveUser(CreatePrincipal(aadId: "aad-new", email: "alice@example.com"));

        Assert.IsNotNull(user);
        Assert.AreEqual(1, user.Id);
        Assert.AreEqual(1, fake.SetAadUserIdCallCount, "SetAadUserId should have been attempted.");
        Assert.IsFalse(fake.AadUserIds.ContainsKey(1), "AadUserId should NOT have been set (it threw).");
    }

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

    [TestMethod]
    public void ResolveUser_ConcurrentMigration_SecondCallUsesAadId()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice@example.com", 1));
        var resolver = new UserResolver(fake, NullLogger<UserResolver>.Instance);

        // First call: finds by email, lazy-migrates AadUserId
        var user1 = resolver.ResolveUser(CreatePrincipal(aadId: "aad-123", email: "alice@example.com"));
        Assert.IsNotNull(user1);
        Assert.AreEqual(1, fake.SetAadUserIdCallCount);
        Assert.AreEqual("aad-123", fake.AadUserIds[1]);

        // Second call: should find by AAD ID directly, no email fallback needed
        var user2 = resolver.ResolveUser(CreatePrincipal(aadId: "aad-123", email: "alice@example.com"));
        Assert.IsNotNull(user2);
        Assert.AreEqual(1, user2.Id);
        Assert.AreEqual(1, fake.SetAadUserIdCallCount, "Should NOT call SetAadUserId again — already migrated.");
        Assert.AreEqual(2, fake.GetUserByAadIdCallCount, "AAD lookup should be attempted on both calls.");
        Assert.AreEqual(1, fake.GetUserByNameCallCount, "Second call should NOT fall back to email lookup.");
    }
}
