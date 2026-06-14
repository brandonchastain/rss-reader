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

    private DateTime startupTime = DateTime.UtcNow;

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

        string[] agents = ["rss.brandonchastain.com/1.1", "curl/7.79.1"];
        feedValidators.TryGetValue(url, out var known);

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
                    return freshItems;
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

        return freshItems;
    }
}
