using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using RssApp.Contracts;

namespace RssApp.Data;

public sealed class CachingUserRepository : IUserRepository
{
    private readonly IUserRepository _inner;
    private readonly IMemoryCache _cache;

    // Guards _evictionCts so reads and swaps are atomic.
    private readonly object _lock = new();

    // All cache entries are linked to this token.  When a user is added we
    // cancel the current CTS (evicting every linked entry at once) and replace
    // it with a fresh one so subsequent entries get a clean token.
    private CancellationTokenSource _evictionCts = new();

    public CachingUserRepository(IUserRepository inner, IMemoryCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    // --- Cache key helpers ---

    private static string UserNameKey(string username) => $"user:name:{username}";
    private static string UserIdKey(int userId) => $"user:id:{userId}";
    private static string UserAadIdKey(string aadId) => $"user:aad:{aadId}";
    private const string AllUsersKey = "users:all";

    // --- Read methods (cache-aside) ---

    public RssUser GetUserByName(string username)
    {
        var key = UserNameKey(username);
        if (_cache.TryGetValue(key, out RssUser cached))
            return cached!;

        var user = _inner.GetUserByName(username);

        // Don't cache null — a miss means "user not found yet" and should
        // re-query the database on the next call.
        if (user is not null)
            SetWithEvictionToken(key, user);

        return user!;
    }

    public RssUser GetUserById(int userId)
    {
        var key = UserIdKey(userId);
        if (_cache.TryGetValue(key, out RssUser cached))
            return cached!;

        var user = _inner.GetUserById(userId);

        if (user is not null)
            SetWithEvictionToken(key, user);

        return user!;
    }

    public RssUser GetUserByAadId(string aadUserId)
    {
        var key = UserAadIdKey(aadUserId);
        if (_cache.TryGetValue(key, out RssUser cached))
            return cached!;

        var user = _inner.GetUserByAadId(aadUserId);

        if (user is not null)
            SetWithEvictionToken(key, user);

        return user!;
    }

    public IEnumerable<RssUser> GetAllUsers()
    {
        if (_cache.TryGetValue(AllUsersKey, out List<RssUser> cached))
            return cached!;

        // Materialize to List<RssUser> before caching — prevents caching a dead lazy enumerator.
        var result = _inner.GetAllUsers().ToList();
        SetWithEvictionToken(AllUsersKey, result);
        return result;
    }

    // --- Write methods (evict all, then seed the new user) ---

    public RssUser AddUser(string username, int? id = null)
    {
        var user = _inner.AddUser(username, id);
        EvictAll();

        // Seed the cache with the newly created user to avoid an immediate DB
        // round-trip on the next lookup.
        if (user is not null)
        {
            SetWithEvictionToken(UserNameKey(user.Username), user);
            SetWithEvictionToken(UserIdKey(user.Id), user);
        }

        return user!;
    }

    public void SetAadUserId(int userId, string aadUserId)
    {
        _inner.SetAadUserId(userId, aadUserId);
        EvictAll();
    }

    // --- Helpers ---

    private void EvictAll()
    {
        // Atomically swap in a fresh CTS so entries set after this point won't
        // be evicted by the cancellation we're about to issue.
        CancellationTokenSource oldCts;
        lock (_lock)
        {
            oldCts = _evictionCts;
            _evictionCts = new CancellationTokenSource();
        }

        // Cancelling the old token evicts every cache entry that was linked to it.
        oldCts.Cancel();
        oldCts.Dispose();
    }

    private void SetWithEvictionToken<T>(string key, T value)
    {
        // Capture the token under the lock so we always pair the entry with the
        // currently-active CTS, even if another thread swaps it out immediately
        // after (worst case: the entry evicts on the next AddUser, which is fine).
        CancellationToken token;
        lock (_lock)
        {
            token = _evictionCts.Token;
        }

        _cache.Set(key, value, new MemoryCacheEntryOptions()
            .AddExpirationToken(new CancellationChangeToken(token)));
    }
}
