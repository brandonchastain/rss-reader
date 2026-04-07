using Microsoft.Extensions.Caching.Memory;
using RssApp.Contracts;
using RssApp.Data;

namespace SerializerTests;

// ---------------------------------------------------------------------------
// Manual fake — no Moq dependency
// ---------------------------------------------------------------------------

internal sealed class FakeUserRepository : IUserRepository
{
    // Backing store — tests can pre-populate this to simulate existing data.
    public List<RssUser> Users { get; } = new();
    public Dictionary<int, string> AadUserIds { get; } = new();

    // Call counters so tests can assert how many times each method was invoked.
    public int AddUserCallCount { get; private set; }
    public int GetUserByNameCallCount { get; private set; }
    public int GetUserByIdCallCount { get; private set; }
    public int GetUserByAadIdCallCount { get; private set; }
    public int SetAadUserIdCallCount { get; private set; }
    public int GetAllUsersCallCount { get; private set; }

    // When true, SetAadUserId throws to simulate a DB constraint violation.
    public bool ThrowOnSetAadUserId { get; set; }

    private int _nextId = 1;

    public RssUser AddUser(string username, int? id = null)
    {
        AddUserCallCount++;
        var user = new RssUser(username, id ?? _nextId++);
        Users.Add(user);
        return user;
    }

    public RssUser GetUserByName(string username)
    {
        GetUserByNameCallCount++;
        return Users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.Ordinal))!;
    }

    public RssUser GetUserById(int userId)
    {
        GetUserByIdCallCount++;
        return Users.FirstOrDefault(u => u.Id == userId)!;
    }

    public RssUser GetUserByAadId(string aadUserId)
    {
        GetUserByAadIdCallCount++;
        var userId = AadUserIds.FirstOrDefault(kvp => kvp.Value == aadUserId);
        if (userId.Value != null)
        {
            return Users.FirstOrDefault(u => u.Id == userId.Key)!;
        }
        return null!;
    }

    public void SetAadUserId(int userId, string aadUserId)
    {
        SetAadUserIdCallCount++;
        if (ThrowOnSetAadUserId)
            throw new InvalidOperationException("Simulated DB constraint violation");
        AadUserIds[userId] = aadUserId;
    }

    public void UpdateUsername(int userId, string newUsername)
    {
        var user = Users.FirstOrDefault(u => u.Id == userId);
        if (user != null)
            user.Username = newUsername;
    }

    public IEnumerable<RssUser> GetAllUsers()
    {
        GetAllUsersCallCount++;
        // Return a new list each time so lazy-vs-materialized bugs surface clearly.
        return Users.ToList();
    }

    public void DeleteUser(int userId)
    {
        Users.RemoveAll(u => u.Id == userId);
    }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

file static class UserCacheFactory
{
    public static IMemoryCache Create() => new MemoryCache(new MemoryCacheOptions());
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[TestClass]
public sealed class CachingUserRepositoryTests
{
    // 1. GetUserByName — second call hits the cache, not the inner repository.
    [TestMethod]
    public void GetUserByName_ReturnsCachedValueOnSecondCall()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice", 1));

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        var first = sut.GetUserByName("alice");
        var second = sut.GetUserByName("alice");

        Assert.AreEqual(1, fake.GetUserByNameCallCount, "Inner should be called exactly once.");
        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
    }

    // 2. GetUserById — second call hits the cache, not the inner repository.
    [TestMethod]
    public void GetUserById_ReturnsCachedValueOnSecondCall()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice", 1));

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        var first = sut.GetUserById(1);
        var second = sut.GetUserById(1);

        Assert.AreEqual(1, fake.GetUserByIdCallCount, "Inner should be called exactly once.");
        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
    }

    // 3. GetAllUsers — second call hits the cache, not the inner repository.
    [TestMethod]
    public void GetAllUsers_ReturnsCachedValueOnSecondCall()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice", 1));

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        var first = sut.GetAllUsers().ToList();
        var second = sut.GetAllUsers().ToList();

        Assert.AreEqual(1, fake.GetAllUsersCallCount, "Inner should be called exactly once.");
        Assert.AreEqual(first.Count, second.Count);
        Assert.AreEqual(first[0].Id, second[0].Id);
    }

    // 4. AddUser — should evict the GetUserByName cache so the next call
    //    refreshes from the inner repository.
    [TestMethod]
    public void AddUser_InvalidatesGetUserByNameCache()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice", 1));

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        // Prime the cache.
        _ = sut.GetUserByName("alice");
        Assert.AreEqual(1, fake.GetUserByNameCallCount);

        // Mutation should bust the cache via CTS cancellation.
        sut.AddUser("bob");

        // Next read must hit inner again.
        _ = sut.GetUserByName("alice");
        Assert.AreEqual(2, fake.GetUserByNameCallCount, "Inner should be called again after AddUser.");
    }

    // 5. AddUser — should evict the GetAllUsers cache so the next call
    //    refreshes from the inner repository.
    [TestMethod]
    public void AddUser_InvalidatesGetAllUsersCache()
    {
        var fake = new FakeUserRepository();
        fake.Users.Add(new RssUser("alice", 1));

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        // Prime the cache.
        _ = sut.GetAllUsers().ToList();
        Assert.AreEqual(1, fake.GetAllUsersCallCount);

        // Mutation should bust the cache.
        sut.AddUser("bob");

        // Next read must hit inner again.
        _ = sut.GetAllUsers().ToList();
        Assert.AreEqual(2, fake.GetAllUsersCallCount, "Inner should be called again after AddUser.");
    }

    // 6. GetUserByName — a null result from inner must NOT be cached; subsequent
    //    calls must re-query the inner repository each time.
    [TestMethod]
    public void GetUserByName_ReturnsNullFromInner_DoesNotCache()
    {
        // Empty repository — all name lookups return null.
        var fake = new FakeUserRepository();

        var sut = new CachingUserRepository(fake, UserCacheFactory.Create());

        var first = sut.GetUserByName("ghost");
        var second = sut.GetUserByName("ghost");

        Assert.IsNull(first);
        Assert.IsNull(second);
        Assert.AreEqual(2, fake.GetUserByNameCallCount,
            "Inner must be called both times; null must not be cached.");
    }
}
