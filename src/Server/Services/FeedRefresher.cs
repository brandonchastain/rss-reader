using RssApp.Contracts;
using RssApp.Serialization;
using RssApp.Data;
using RssApp.ComponentServices;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
    private readonly IFeedValidatorStore validatorStore;
    private readonly ConcurrentDictionary<int, UserRefreshState> refreshStates = new();

    // In-memory HTTP cache validators per feed URL, used for conditional GET
    // (If-None-Match / If-Modified-Since). Acts as a write-through cache in front
    // of IFeedValidatorStore: a cache miss (e.g. the first fetch of a feed after a
    // restart) loads the persisted validator, so we resume sending conditional GETs
    // and earn 304s instead of cold-fetching every feed (no restart thundering herd).
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

    private readonly IFeedScheduleStore scheduleStore;

    // In-memory per-URL next-earliest-fetch schedule, write-through to scheduleStore.
    // Loaded once from the store (so the cadence survives restarts) and updated after
    // every fetch. Both the scheduler and manual refreshes consult it to decide whether
    // a URL is due, which is what dedups a feed shared by many users down to one fetch.
    private readonly ConcurrentDictionary<string, DateTimeOffset> nextEarliestByUrl = new();
    private volatile bool scheduleLoaded;
    private readonly object scheduleLoadLock = new();

    // In-flight fetch coalescing: concurrent callers for the same URL (e.g. a manual
    // refresh racing the scheduler) share one HTTP fetch instead of each hitting the origin.
    private readonly ConcurrentDictionary<string, Task<FeedFetchResult>> inflightFetches = new();

    // Placeholder used when parsing a feed not on behalf of a specific user; the real
    // UserId is assigned per subscriber during fan-out, so this value is never persisted.
    private static readonly RssUser SchedulerUser = new("scheduler", 0);

    private static readonly Regex TtlRegex =
        new(@"<ttl>\s*(\d+)\s*</ttl>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UpdatePeriodRegex =
        new(@"<sy:updatePeriod>\s*(hourly|daily|weekly|monthly|yearly)\s*</sy:updatePeriod>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UpdateFrequencyRegex =
        new(@"<sy:updateFrequency>\s*(\d+)\s*</sy:updateFrequency>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum FetchOutcome { Fetched, NotModified, BackedOff, Failed }

    private sealed class FeedFetchResult
    {
        public FetchOutcome Outcome { get; init; }
        public List<NewsFeedItem> Items { get; init; } = new();
        public TimeSpan? AdvertisedInterval { get; init; }
        public DateTimeOffset? BackoffUntil { get; init; }
    }

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
        ThumbnailResolver thumbnailResolver,
        IFeedValidatorStore validatorStore,
        IFeedScheduleStore scheduleStore)
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
        this.validatorStore = validatorStore;
        this.scheduleStore = scheduleStore;
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
        // A newly added feed: force a fetch and write its items for this user right
        // away (don't wait for the scheduler), independent of the per-URL schedule.
        await FetchAndWriteForFeedAsync(feed, force: true, CancellationToken.None);
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
                        // Route through the shared per-URL coordinator: fetches are
                        // coalesced + deduped, items fan out to every subscriber, and
                        // we take this user's new-item count for the refresh status.
                        var counts = await FetchAndFanOutAsync(feed.Href, force: false, token);
                        counts.TryGetValue(user.Id, out added);
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

    public async Task RunSchedulerTickAsync(CancellationToken token)
    {
        EnsureScheduleLoaded();

        List<string> urls;
        try
        {
            urls = this.persistedFeeds.GetAllDistinctFeedUrls().ToList();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Scheduler: failed to enumerate feed URLs.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var due = urls.Where(u => !nextEarliestByUrl.TryGetValue(u, out var next) || next <= now).ToList();
        if (due.Count == 0)
        {
            return;
        }

        this.logger.LogInformation("Scheduler: {due} of {total} distinct feeds due for refresh.", due.Count, urls.Count);

        using var gate = new SemaphoreSlim(Math.Max(1, this.config.RefreshFetchConcurrency));
        var tasks = due.Select(async url =>
        {
            await gate.WaitAsync(token);
            try
            {
                await FetchAndFanOutAsync(url, force: false, token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Scheduler: error refreshing {url}", url);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Fetches a URL once (if due, unless forced) and fans the parsed items out to
    /// every subscriber, returning each subscriber's new-item count. This is the one
    /// place a feed's items are produced, so a feed shared by N users is fetched once.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, int>> FetchAndFanOutAsync(string url, bool force, CancellationToken token)
    {
        var counts = new Dictionary<int, int>();
        if (!force && !IsDue(url))
        {
            return counts;
        }

        var result = await FetchParsedItemsAsync(url, token);
        UpdateSchedule(url, result);

        if (result.Outcome != FetchOutcome.Fetched || result.Items.Count == 0)
        {
            return counts;
        }

        var subscribers = this.persistedFeeds.GetFeedsByUrl(url).ToList();
        foreach (var feed in subscribers)
        {
            var userItems = result.Items.Select(t => CloneForSubscriber(t, feed.UserId, feed.Tags)).ToList();
            counts[feed.UserId] = await this.newsFeedItemStore.AddItemsAsync(userItems);
        }

        return counts;
    }

    /// <summary>
    /// Fetches a URL (if due, unless forced) and writes its items for a single feed's
    /// user. Used when a user adds a feed so their items appear immediately, without
    /// waiting for the scheduler and without disturbing the shared per-URL schedule.
    /// </summary>
    private async Task<int> FetchAndWriteForFeedAsync(NewsFeed feed, bool force, CancellationToken token)
    {
        var url = feed.Href;
        if (!force && !IsDue(url))
        {
            return 0;
        }

        var result = await FetchParsedItemsAsync(url, token);

        if (result.Outcome != FetchOutcome.Fetched || result.Items.Count == 0)
        {
            return 0;
        }

        var userItems = result.Items.Select(t => CloneForSubscriber(t, feed.UserId, feed.Tags)).ToList();
        return await this.newsFeedItemStore.AddItemsAsync(userItems);
    }

    /// <summary>Produces a per-subscriber copy of a parsed item with the user's id and tags.</summary>
    private static NewsFeedItem CloneForSubscriber(NewsFeedItem template, int userId, ICollection<string> tags)
    {
        return new NewsFeedItem(
            template.Id,
            userId,
            template.Title,
            template.Href,
            template.CommentsHref,
            template.PublishDate,
            template.Content,
            template.ThumbnailUrl)
        {
            FeedUrl = template.FeedUrl,
            PublishDateOrder = template.PublishDateOrder,
            FeedTags = tags ?? new List<string>(),
        };
    }

    // --- Per-URL schedule (write-through cache over IFeedScheduleStore) ---

    private void EnsureScheduleLoaded()
    {
        if (scheduleLoaded) return;
        lock (scheduleLoadLock)
        {
            if (scheduleLoaded) return;
            try
            {
                foreach (var kv in this.scheduleStore.GetSchedule())
                {
                    nextEarliestByUrl[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to load persisted feed schedule; treating all feeds as due.");
            }
            scheduleLoaded = true;
        }
    }

    private bool IsDue(string url)
    {
        EnsureScheduleLoaded();
        return !nextEarliestByUrl.TryGetValue(url, out var next) || next <= DateTimeOffset.UtcNow;
    }

    private void UpdateSchedule(string url, FeedFetchResult result)
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan interval;
        DateTimeOffset nextEarliest;

        if (result.Outcome == FetchOutcome.BackedOff || result.Outcome == FetchOutcome.Failed)
        {
            // Align the schedule with the politeness backoff so the scheduler doesn't
            // re-select a struggling feed every tick.
            interval = this.config.FeedRefreshInterval;
            nextEarliest = result.BackoffUntil ?? now + interval;
        }
        else
        {
            interval = ClampInterval(result.AdvertisedInterval ?? this.config.FeedRefreshInterval);
            nextEarliest = now + interval;
        }

        nextEarliestByUrl[url] = nextEarliest;
        this.scheduleStore.Record(url, now, nextEarliest, interval);
    }

    private TimeSpan ClampInterval(TimeSpan interval)
    {
        if (interval < this.config.FeedRefreshIntervalFloor) return this.config.FeedRefreshIntervalFloor;
        if (interval > this.config.FeedRefreshIntervalMax) return this.config.FeedRefreshIntervalMax;
        return interval;
    }

    /// <summary>
    /// Coalesces concurrent fetches of the same URL: callers that arrive while a
    /// fetch is in flight share its result instead of each hitting the origin.
    /// </summary>
    private Task<FeedFetchResult> FetchParsedItemsAsync(string url, CancellationToken token)
    {
        // Share a fetch only while it is genuinely in flight: concurrent callers for
        // the same URL join the running task, but once it completes the next caller
        // starts a fresh fetch (the completed entry is replaced, not reused).
        return inflightFetches.AddOrUpdate(
            url,
            u => FetchParsedItemsCoreAsync(u, token),
            (u, existing) => existing.IsCompleted ? FetchParsedItemsCoreAsync(u, token) : existing);
    }

    /// <summary>
    /// Fetches and parses a feed once, applying all politeness (conditional GET,
    /// per-host throttle, Retry-After/backoff). Returns parsed items with the feed
    /// URL + resolved thumbnail set but no UserId — that is assigned per subscriber
    /// during fan-out. The result's outcome tells the caller whether to fan out and
    /// how to schedule the next fetch.
    /// </summary>
    private async Task<FeedFetchResult> FetchParsedItemsCoreAsync(string url, CancellationToken token)
    {
        if (!EnableHttpLookup)
        {
            return new FeedFetchResult { Outcome = FetchOutcome.NotModified };
        }

        var health = feedHealth.GetOrAdd(url, _ => new FeedFetchHealth());

        // Politeness: if this feed is in a backoff window (from a prior failure
        // or an origin Retry-After), don't touch the origin yet.
        var backoffUntil = health.NextEarliestFetch;
        if (backoffUntil.HasValue && DateTimeOffset.UtcNow < backoffUntil.Value)
        {
            this.logger.LogDebug(
                "Skipping feed {url}; backing off until {until} ({failures} consecutive failures).",
                url, backoffUntil.Value, health.ConsecutiveFailures);
            return new FeedFetchResult { Outcome = FetchOutcome.BackedOff, BackoffUntil = backoffUntil };
        }

        string[] agents = ["rss.brandonchastain.com/1.1", "curl/7.79.1"];
        var known = GetValidators(url);

        // Bound concurrent hits to this origin host, independent of the global gate.
        var hostGate = GetHostGate(url);
        await hostGate.WaitAsync(token);

        List<NewsFeedItem> parsed = null;
        TimeSpan? advertisedInterval = null;
        TimeSpan? originRetryAfter = null;
        bool notModified = false;

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
                        // Nothing changed since last fetch — a 304 is a healthy response.
                        notModified = true;
                        break;
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
                    // Persisted (keyed by URL) so they also survive a restart.
                    var newEtag = httpRes.Headers.ETag;
                    var newLastModified = httpRes.Content.Headers.LastModified;
                    if (newEtag != null || newLastModified != null)
                    {
                        feedValidators[url] = (newEtag, newLastModified);
                        this.validatorStore.Set(url, newEtag?.ToString(), newLastModified);
                    }

                    var items = this.deserializer.FromString(response, SchedulerUser).ToList();
                    foreach (var item in items)
                    {
                        item.FeedUrl = url;
                        item.ThumbnailUrl = this.thumbnailResolver.Resolve(item);
                    }
                    parsed = items;
                    advertisedInterval = TryParseAdvertisedInterval(response);

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
        }
        finally
        {
            hostGate.Release();
        }

        if (notModified)
        {
            health.RecordSuccess();
            return new FeedFetchResult { Outcome = FetchOutcome.NotModified };
        }

        if (parsed != null)
        {
            health.RecordSuccess();
            return new FeedFetchResult
            {
                Outcome = FetchOutcome.Fetched,
                Items = parsed,
                AdvertisedInterval = advertisedInterval,
            };
        }

        // Nothing usable came back. Record the failure and schedule the next earliest
        // fetch — an explicit Retry-After always wins; otherwise exponential backoff.
        var failures = health.RecordFailure();
        var delay = originRetryAfter ?? ComputeBackoffDelay(failures);
        var until = DateTimeOffset.UtcNow + delay;
        health.SetNextEarliestFetch(until);
        this.logger.LogWarning(
            "Feed {url} fetch failed ({failures} consecutive); next attempt no earlier than {delay} from now.",
            url, failures, delay);
        return new FeedFetchResult { Outcome = FetchOutcome.Failed, BackoffUntil = until };
    }

    /// <summary>
    /// Parses a feed's advertised polling interval from <c>&lt;ttl&gt;</c> (minutes)
    /// or the syndication module (<c>sy:updatePeriod</c> + optional
    /// <c>sy:updateFrequency</c>). Returns null when the feed advertises neither.
    /// </summary>
    private static TimeSpan? TryParseAdvertisedInterval(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;

        var ttl = TtlRegex.Match(xml);
        if (ttl.Success && int.TryParse(ttl.Groups[1].Value, out var minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        var period = UpdatePeriodRegex.Match(xml);
        if (period.Success)
        {
            TimeSpan basePeriod = period.Groups[1].Value.ToLowerInvariant() switch
            {
                "hourly" => TimeSpan.FromHours(1),
                "daily" => TimeSpan.FromDays(1),
                "weekly" => TimeSpan.FromDays(7),
                "monthly" => TimeSpan.FromDays(30),
                "yearly" => TimeSpan.FromDays(365),
                _ => TimeSpan.FromDays(1),
            };
            var freqMatch = UpdateFrequencyRegex.Match(xml);
            int frequency = freqMatch.Success && int.TryParse(freqMatch.Groups[1].Value, out var f) && f > 0 ? f : 1;
            return basePeriod / frequency;
        }

        return null;
    }

    /// <summary>
    /// Returns the conditional-GET validators for a feed URL. Serves from the
    /// in-memory cache when present; on a miss (e.g. the first fetch after a
    /// restart) loads the persisted validator from the store and warms the cache,
    /// so we still send a conditional GET and can earn a 304 instead of a cold fetch.
    /// </summary>
    private (EntityTagHeaderValue ETag, DateTimeOffset? LastModified) GetValidators(string url)
    {
        if (feedValidators.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var persisted = this.validatorStore.Get(url);
        EntityTagHeaderValue etag = null;
        if (persisted != null && !string.IsNullOrEmpty(persisted.ETag))
        {
            EntityTagHeaderValue.TryParse(persisted.ETag, out etag);
        }

        var loaded = (etag, persisted?.LastModified);
        feedValidators[url] = loaded;
        return loaded;
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
