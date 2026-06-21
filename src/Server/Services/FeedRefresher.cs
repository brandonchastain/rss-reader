using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;
using RssApp.ComponentServices;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using RssApp.Config;
using RssReader.Server.Services;

namespace RssApp.RssClient;

public class FeedRefresher : IFeedRefresher
{
    private static readonly bool EnableHttpLookup = true;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly RssDeserializer deserializer;
    private readonly ILogger<FeedRefresher> logger;
    private readonly IFeedRepository persistedFeeds;
    private readonly IItemRepository newsFeedItemStore;
    private readonly IUserRepository userStore;
    private readonly BackgroundWorkQueue backgroundWorkQueue;
    private readonly RssAppConfig config;
    private readonly ThumbnailResolver thumbnailResolver;
    private readonly ConcurrentDictionary<int, UserRefreshState> refreshStates = new();

    // In-memory HTTP cache validators per feed URL, used for conditional GET
    // (If-None-Match / If-Modified-Since). Lets repeat refreshes within the
    // process lifetime skip unchanged feeds via a 304. Not persisted: a cold
    // start simply re-fetches everything, which is the desired behavior anyway.
    private readonly ConcurrentDictionary<string, (EntityTagHeaderValue ETag, DateTimeOffset? LastModified)> feedValidators = new();

    // Per-feed-URL fetch health: tracks consecutive failures and a backoff
    // window. After a failed/non-OK fetch we refuse to re-hit the origin until
    // the backoff (or an origin-supplied Retry-After) elapses, so repeated user
    // refreshes don't hammer a struggling or rate-limiting host. In-memory only.
    private readonly ConcurrentDictionary<string, FeedFetchHealth> feedHealth = new();

    // Per-origin-host concurrency gates. Even when the global RefreshFetchConcurrency
    // gate would allow more, we never have more than MaxConcurrentFetchesPerHost
    // requests in flight to the same host at once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> hostGates = new();

    private DateTime startupTime = DateTime.UtcNow;

    /// <summary>
    /// Per-feed-URL fetch health. Thread-safe via Interlocked so concurrent
    /// refreshes touching the same URL stay consistent.
    /// </summary>
    private sealed class FeedFetchHealth
    {
        private int _consecutiveFailures;
        private long _nextEarliestFetchTicks; // DateTimeOffset.UtcTicks; 0 = no backoff

        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public DateTimeOffset? NextEarliestFetch
        {
            get
            {
                var ticks = Interlocked.Read(ref _nextEarliestFetchTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public void RecordSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _nextEarliestFetchTicks, 0);
        }

        public int RecordFailure() => Interlocked.Increment(ref _consecutiveFailures);

        public void SetNextEarliestFetch(DateTimeOffset when)
            => Interlocked.Exchange(ref _nextEarliestFetchTicks, when.UtcTicks);
    }

    /// <summary>
    /// Per-user refresh state. Thread-safe via Interlocked/volatile so the
    /// parallel per-feed completions during a single refresh stay consistent.
    /// </summary>
    private class UserRefreshState
    {
        private int _pendingFeeds;
        private int _newItemCount;
        private volatile bool _isRefreshing;
        private DateTime? _lastRefreshTime;

        public bool IsRefreshing => _isRefreshing;
        public int PendingFeeds => Volatile.Read(ref _pendingFeeds);
        public int NewItemCount => Volatile.Read(ref _newItemCount);
        public bool HasNewItems => NewItemCount > 0;
        public DateTime? LastRefreshTime => _lastRefreshTime;

        public void StartRefresh(int feedCount)
        {
            Interlocked.Exchange(ref _newItemCount, 0);
            Interlocked.Exchange(ref _pendingFeeds, feedCount);
            _isRefreshing = true;
        }

        public void CompleteFeed(int newItems)
        {
            if (newItems > 0) Interlocked.Add(ref _newItemCount, newItems);
            var remaining = Interlocked.Decrement(ref _pendingFeeds);
            if (remaining <= 0)
            {
                _lastRefreshTime = DateTime.UtcNow;
                _isRefreshing = false;
            }
        }

        public void FailRefresh()
        {
            Interlocked.Exchange(ref _pendingFeeds, 0);
            _isRefreshing = false;
        }

        public void ClearNewItems()
        {
            Interlocked.Exchange(ref _newItemCount, 0);
        }
    }

    public FeedRefresher(
        IHttpClientFactory httpClientFactory,
        RssDeserializer deserializer,
        ILogger<FeedRefresher> logger,
        IFeedRepository persistedFeeds,
        IItemRepository newsFeedItemStore,
        IUserRepository userStore,
        BackgroundWorkQueue backgroundWorkQueue,
        RssAppConfig config,
        ThumbnailResolver thumbnailResolver)
    {
        this.httpClientFactory = httpClientFactory;
        this.deserializer = deserializer;
        this.logger = logger;
        this.persistedFeeds = persistedFeeds;
        this.newsFeedItemStore = newsFeedItemStore;
        this.userStore = userStore;
        this.backgroundWorkQueue = backgroundWorkQueue;
        this.config = config;
        this.thumbnailResolver = thumbnailResolver;
    }

    public void ResetRefreshCooldown()
    {
        refreshStates.Clear();
    }

    public RefreshStatusResponse GetRefreshStatus(RssUser user)
    {
        var state = refreshStates.GetOrAdd(user.Id, _ => new UserRefreshState());
        return new RefreshStatusResponse
        {
            HasNewItems = state.HasNewItems,
            IsRefreshing = state.IsRefreshing,
            PendingFeeds = state.PendingFeeds,
            NewItemCount = state.NewItemCount
        };
    }

    public async Task AddFeedAsync(NewsFeed feed)
    {
        await ReloadCachedItemsAsync(feed);
    }

    public async Task RefreshAsync(RssUser user)
    {
        var state = refreshStates.GetOrAdd(user.Id, _ => new UserRefreshState());

        // If a refresh is already running for this user, don't start another.
        if (state.IsRefreshing)
        {
            return;
        }

        bool isJustStarted = this.startupTime + this.config.CacheReloadStartupDelay > DateTime.UtcNow;
        bool isRecentRefresh = state.LastRefreshTime + this.config.CacheReloadInterval > DateTime.UtcNow;

        if (isJustStarted || isRecentRefresh)
        {
            // Cooldown active — no refresh runs. Clear any stale new-item count so
            // the status endpoint honestly reports "up to date" instead of echoing
            // a previous refresh's count.
            state.ClearNewItems();
            return;
        }

        try
        {
            var feeds = this.persistedFeeds.GetFeeds(user).ToList();

            if (feeds.Count == 0)
            {
                state.ClearNewItems();
                return;
            }

            state.StartRefresh(feeds.Count);

            // One queued job orchestrates the whole refresh and returns immediately
            // to the caller (controller responds 202). The job fetches feeds with
            // bounded concurrency; DB writes remain serialized inside the item repo.
            await this.backgroundWorkQueue.QueueBackgroundWorkItemAsync(async token =>
            {
                using var gate = new SemaphoreSlim(Math.Max(1, this.config.RefreshFetchConcurrency));

                var tasks = feeds.Select(async feed =>
                {
                    await gate.WaitAsync(token);
                    int added = 0;
                    try
                    {
                        added = await ReloadCachedItemsAsync(feed, token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown — let the finally still mark the feed complete.
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "Error reloading feed: {feed}", feed.Href);
                    }
                    finally
                    {
                        state.CompleteFeed(added);
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks);
            });
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error reloading cache");
            state.FailRefresh();
        }
    }

    /// <summary>
    /// Fetches fresh items from a feed and passes ALL of them to AddItemsAsync.
    /// Deduplication is handled inside AddItemsAsync via INSERT OR IGNORE,
    /// so no per-item GetItem() calls are needed here.
    /// Returns the number of new items inserted.
    /// </summary>
    private async Task<int> ReloadCachedItemsAsync(NewsFeed feed, CancellationToken token = default)
    {
        var url = feed.Href;
        var user = this.userStore.GetUserById(feed.UserId);

        if (user == null)
        {
            this.logger.LogError("User not found: {userId}", feed.UserId);
            return 0;
        }

        var freshItems = await this.FetchItemsFromFeedAsync(user, url, token);

        foreach (var item in freshItems)
        {
            item.FeedUrl = url;
            item.FeedTags = feed.Tags;
            item.ThumbnailUrl = this.thumbnailResolver.Resolve(item);
        }

        if (freshItems.Any())
        {
            return await this.newsFeedItemStore.AddItemsAsync(freshItems);
        }

        return 0;
    }

    private async Task<HashSet<NewsFeedItem>> FetchItemsFromFeedAsync(RssUser user, string url, CancellationToken token = default)
    {
        var freshItems = new HashSet<NewsFeedItem>();
        if (!EnableHttpLookup)
        {
            return freshItems;
        }

        var health = feedHealth.GetOrAdd(url, _ => new FeedFetchHealth());

        // Politeness: if this feed is in a backoff window (from a prior failure
        // or an origin Retry-After), don't touch the origin yet.
        var nextEarliest = health.NextEarliestFetch;
        if (nextEarliest.HasValue && DateTimeOffset.UtcNow < nextEarliest.Value)
        {
            this.logger.LogDebug(
                "Skipping feed {url}; backing off until {until} ({failures} consecutive failures).",
                url, nextEarliest.Value, health.ConsecutiveFailures);
            return freshItems;
        }

        string[] agents = ["rss.brandonchastain.com/1.1", "curl/7.79.1"];
        feedValidators.TryGetValue(url, out var known);

        // Bound concurrent hits to this origin host, independent of the global gate.
        var hostGate = GetHostGate(url);
        await hostGate.WaitAsync(token);

        bool succeeded = false;
        TimeSpan? originRetryAfter = null;

        try
        {
            foreach (string agent in agents)
            {
                string response = null;
                try
                {
                    using var httpClient = httpClientFactory.CreateClient("RssClient");
                    using var browserRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    browserRequest.Headers.UserAgent.ParseAdd(agent);
                    browserRequest.Headers.Accept.ParseAdd("text/xml");
                    browserRequest.Headers.Accept.ParseAdd("application/xml");
                    browserRequest.Headers.Accept.ParseAdd("application/rss+xml");
                    browserRequest.Headers.Accept.ParseAdd("application/atom+xml");

                    // Conditional GET: ask the origin to skip sending an unchanged feed.
                    if (known.ETag != null) browserRequest.Headers.IfNoneMatch.Add(known.ETag);
                    if (known.LastModified != null) browserRequest.Headers.IfModifiedSince = known.LastModified;

                    using var httpRes = await httpClient.SendAsync(browserRequest, token);

                    if (httpRes.StatusCode == HttpStatusCode.NotModified)
                    {
                        // Nothing changed since last fetch — skip parse + DB write entirely.
                        // A 304 is a healthy response: clear any prior backoff.
                        succeeded = true;
                        return freshItems;
                    }

                    // Rate-limited or service-unavailable: respect the origin's wishes
                    // and stop immediately (other user-agents won't fare better against
                    // the same host). Honor Retry-After if present.
                    if (httpRes.StatusCode == HttpStatusCode.TooManyRequests ||
                        httpRes.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        originRetryAfter = GetRetryAfterDelay(httpRes);
                        this.logger.LogWarning(
                            "Feed {url} returned {status}; backing off (Retry-After: {retryAfter}).",
                            url, (int)httpRes.StatusCode, originRetryAfter);
                        break;
                    }

                    // Other non-success (403/404/5xx): this agent failed. Fall through
                    // to try the next user-agent (some hosts block by UA), and if none
                    // succeed we record a failure + backoff below.
                    if (!httpRes.IsSuccessStatusCode)
                    {
                        this.logger.LogWarning(
                            "Feed {url} returned {status} for agent {agent}.",
                            url, (int)httpRes.StatusCode, agent);
                        continue;
                    }

                    response = await httpRes.Content.ReadAsStringAsync(token);

                    if (string.IsNullOrEmpty(response))
                    {
                        this.logger.LogWarning("Empty response when refreshing feed: {url} (headers: {headers})", url, httpRes.Headers);
                        continue;
                    }

                    // Remember validators so future refreshes can short-circuit with a 304.
                    var newEtag = httpRes.Headers.ETag;
                    var newLastModified = httpRes.Content.Headers.LastModified;
                    if (newEtag != null || newLastModified != null)
                    {
                        feedValidators[url] = (newEtag, newLastModified);
                    }

                    var items = this.deserializer.FromString(response, user);
                    freshItems.UnionWith(items);

                    // It worked. Exit the loop.
                    succeeded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    int len = Math.Min(500, response?.Length ?? 0);
                    this.logger.LogError(ex, "Error reloading feeds. Bad RSS response.\n{url}\n{response}", url, response?.Substring(0, len));
                }
            }
        }
        finally
        {
            hostGate.Release();
        }

        if (succeeded)
        {
            health.RecordSuccess();
        }
        else
        {
            // Record the failure and schedule the next earliest fetch. An explicit
            // Retry-After from the origin always wins; otherwise use exponential backoff.
            var failures = health.RecordFailure();
            var delay = originRetryAfter ?? ComputeBackoffDelay(failures);
            health.SetNextEarliestFetch(DateTimeOffset.UtcNow + delay);
            this.logger.LogWarning(
                "Feed {url} fetch failed ({failures} consecutive); next attempt no earlier than {delay} from now.",
                url, failures, delay);
        }

        return freshItems;
    }

    /// <summary>
    /// Returns the per-origin-host concurrency gate for a feed URL, creating it
    /// on first use. Hosts that can't be parsed share a single fallback gate.
    /// </summary>
    private SemaphoreSlim GetHostGate(string url)
    {
        string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "__unparsed__";
        return hostGates.GetOrAdd(host, _ => new SemaphoreSlim(Math.Max(1, this.config.MaxConcurrentFetchesPerHost)));
    }

    /// <summary>
    /// Reads the Retry-After header (either a delta in seconds or an HTTP-date)
    /// and returns it as a non-negative delay, or null if absent/unparseable.
    /// </summary>
    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null) return null;
        if (retryAfter.Delta.HasValue) return retryAfter.Delta.Value;
        if (retryAfter.Date.HasValue)
        {
            var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// Exponential backoff with +/-20% jitter: base * 2^(failures-1), capped at
    /// the configured maximum. Jitter spreads retries so many failing feeds don't
    /// re-hit their origins in lockstep.
    /// </summary>
    private TimeSpan ComputeBackoffDelay(int failures)
    {
        var baseMs = this.config.FeedBackoffBase.TotalMilliseconds;
        var maxMs = this.config.FeedBackoffMax.TotalMilliseconds;
        // Clamp the exponent so Math.Pow can't overflow to Infinity.
        var exponent = Math.Min(Math.Max(0, failures - 1), 20);
        var grown = baseMs * Math.Pow(2, exponent);
        var capped = Math.Min(grown, maxMs);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4); // 0.8 .. 1.2
        return TimeSpan.FromMilliseconds(capped * jitter);
    }
}
