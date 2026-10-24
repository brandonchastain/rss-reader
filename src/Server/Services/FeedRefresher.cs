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
    private readonly RefreshPacer pacer;
    private readonly ConcurrentDictionary<int, UserRefreshState> refreshStates = new();

    // Per-feed-URL fetch health: tracks consecutive failures and a backoff
    // window. After a failed/non-OK fetch we refuse to re-hit the origin until
    // the backoff (or an origin-supplied Retry-After) elapses, so repeated user
    // refreshes don't hammer a struggling or rate-limiting host. In-memory only.
    private readonly ConcurrentDictionary<string, FeedFetchHealth> feedHealth = new();

    // Per-origin-host concurrency gates. Even when the global RefreshFetchConcurrency
    // gate would allow more, we never have more than MaxConcurrentFetchesPerHost
    // requests in flight to the same host at once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> hostGates = new();

    // In-flight fetch coalescing: concurrent callers for the same URL (e.g. a manual
    // refresh racing the scheduler) share one HTTP fetch instead of each hitting the origin.
    private readonly ConcurrentDictionary<string, Task<FeedFetchResult>> inflightFetches = new();

    // Placeholder used when parsing a feed; posts are shared, so no user id is
    // ever persisted with them.
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
        public string ETag { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public string Error { get; init; }
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

        public FeedFetchHealth()
        {
        }

        public FeedFetchHealth(int consecutiveFailures, DateTimeOffset? nextEarliestFetch)
        {
            _consecutiveFailures = consecutiveFailures;
            _nextEarliestFetchTicks = nextEarliestFetch?.UtcTicks ?? 0;
        }

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
        RefreshPacer pacer)
    {
        this.pacer = pacer;
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

                // One schedule read for the whole refresh instead of one per feed.
                var states = LoadStates();

                var tasks = feeds.Select(async feed =>
                {
                    await gate.WaitAsync(token);
                    int added = 0;
                    try
                    {
                        // A manual refresh asks "is it stale?" rather than "is it due?":
                        // a feed not attempted within the politeness floor is fetched
                        // even if the scheduler would have waited for its full interval.
                        // One fetched moments ago is left alone, and a feed in failure
                        // backoff is still skipped inside the fetch itself.
                        states.TryGetValue(feed.Href, out var feedState);
                        added = await FetchFeedAsync(feed.Href, force: IsStale(feedState), token);
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

    public async Task FetchNewFeedAsync(string url)
    {
        FeedFetchState state;
        try
        {
            state = this.persistedFeeds.GetFeedFetchState(url);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not read fetch state for new feed {url}", url);
            return;
        }

        // Only a row nobody has fetched yet; an existing feed's posts are
        // already there and the schedule owns the next fetch.
        if (state == null || state.LastFetchedUtc != null)
        {
            return;
        }

        try
        {
            await FetchFeedAsync(url, force: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "First fetch of new feed {url} failed; the scheduler will retry.", url);
        }
    }

    public async Task<DateTimeOffset?> RunSchedulerTickAsync(CancellationToken token)
    {
        IReadOnlyList<FeedFetchState> states;
        try
        {
            states = this.persistedFeeds.GetFeedFetchStates() ?? Array.Empty<FeedFetchState>();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Scheduler: failed to enumerate feeds.");
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var due = states.Where(s => IsDue(s, now)).ToList();
        if (due.Count == 0)
        {
            return EarliestNextFetch(states);
        }

        this.logger.LogInformation(
            "Scheduler: {due} of {total} feeds due for refresh, paced at {rate}/s.",
            due.Count, states.Count, this.config.SchedulerFetchesPerSecond);

        // The gate bounds how many fetches are in flight; the pacer spreads their
        // starts over time (taken inside the gate so only a gate's worth of tasks
        // ever hold a timer). A tick with thousands of due feeds is therefore a
        // trickle at SchedulerFetchesPerSecond, not a burst that pins the CPU.
        using var gate = new SemaphoreSlim(Math.Max(1, this.config.RefreshFetchConcurrency));
        var tasks = due.Select(async feed =>
        {
            await gate.WaitAsync(token);
            try
            {
                await this.pacer.WaitForFetchSlotAsync(token);
                await FetchFeedAsync(feed.Url, force: true, token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Scheduler: error refreshing {url}", feed.Url);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        try
        {
            return EarliestNextFetch(this.persistedFeeds.GetFeedFetchStates() ?? Array.Empty<FeedFetchState>());
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Scheduler: failed to re-read the schedule after the pass.");
            return null;
        }
    }

    /// <summary>
    /// Earliest next-fetch time across the feeds. A feed with no schedule yet (just
    /// subscribed, or its fetch failed before the schedule was written) is due now.
    /// </summary>
    private static DateTimeOffset? EarliestNextFetch(IReadOnlyList<FeedFetchState> states)
    {
        if (states.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return states.Min(s => s.NextFetchUtc ?? now);
    }

    /// <summary>
    /// Fetches a feed once (if due, unless forced) and stores its posts once,
    /// returning the number of new posts. This is the one place a feed's posts
    /// are produced; subscribers see them through their subscription, so a feed
    /// shared by N users costs one fetch and one write.
    /// </summary>
    internal async Task<int> FetchFeedAsync(string url, bool force, CancellationToken token)
    {
        if (!force && !IsDue(this.persistedFeeds.GetFeedFetchState(url), DateTimeOffset.UtcNow))
        {
            return 0;
        }

        var result = await FetchParsedItemsAsync(url, token);
        RecordFetchResult(url, result);

        if (result.Outcome != FetchOutcome.Fetched || result.Items.Count == 0)
        {
            return 0;
        }

        return await this.newsFeedItemStore.AddItemsAsync(result.Items);
    }

    // --- Schedule (the Feeds row is the source of truth) ---

    private Dictionary<string, FeedFetchState> LoadStates()
    {
        try
        {
            return (this.persistedFeeds.GetFeedFetchStates() ?? Array.Empty<FeedFetchState>())
                .ToDictionary(s => s.Url, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to load the feed schedule; treating all feeds as due.");
            return new Dictionary<string, FeedFetchState>(StringComparer.Ordinal);
        }
    }

    private static bool IsDue(FeedFetchState state, DateTimeOffset now)
        => state?.NextFetchUtc == null || state.NextFetchUtc <= now;

    /// <summary>
    /// True when the feed has not been fetched (or attempted) within
    /// <see cref="RssAppConfig.FeedRefreshIntervalFloor"/>. Never fetched counts as stale.
    /// </summary>
    internal bool IsStale(string url) => IsStale(this.persistedFeeds.GetFeedFetchState(url));

    private bool IsStale(FeedFetchState state)
        => state?.LastFetchedUtc == null
            || state.LastFetchedUtc.Value + this.config.FeedRefreshIntervalFloor <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Starts a feed's backoff from what the Feeds row recorded, not from zero. The replica
    /// restarts on every deploy and every scale-to-zero, and an in-memory-only counter began
    /// each restart at the shortest delay, so a dead feed was retried about every half hour
    /// (45 failures in a day) instead of backing off to the cap.
    /// </summary>
    private FeedFetchHealth HealthFromPersisted(string url)
    {
        var state = this.persistedFeeds.GetFeedFetchState(url);
        return state is { ConsecutiveFailures: > 0 }
            ? new FeedFetchHealth(state.ConsecutiveFailures, state.NextFetchUtc)
            : new FeedFetchHealth();
    }

    /// <summary>
    /// The reason to show a person: the exception's message plus the innermost one, which is
    /// where the cause lives ("The SSL connection could not be established, see inner
    /// exception." hides "the remote certificate has expired").
    /// </summary>
    internal static string DescribeFailure(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        var outer = ex.Message.Replace(", see inner exception.", string.Empty).Replace(" See inner exception.", string.Empty).TrimEnd('.', ' ');
        return ReferenceEquals(inner, ex) ? ex.Message : $"{outer} - {inner.Message.TrimEnd('.', ' ')}";
    }

    private const int MaxStoredErrorLength = 300;

    private static string TruncateError(string error)
        => error != null && error.Length > MaxStoredErrorLength ? error[..MaxStoredErrorLength] + "…" : error;

    /// <summary>
    /// When the response body is an HTML page rather than a feed, returns a
    /// human-readable reason (naming where a redirect landed), otherwise null.
    /// </summary>
    internal static string DescribeNonFeed(HttpResponseMessage response, string requestedUrl, string body)
    {
        var trimmed = body?.TrimStart() ?? string.Empty;
        var looksLikeHtml =
            trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeHtml)
        {
            return null;
        }

        var final = response.RequestMessage?.RequestUri;
        if (final != null
            && Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requested)
            && !string.Equals(final.GetLeftPart(UriPartial.Path), requested.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase))
        {
            return $"returned a web page, not a feed (redirected to {final.Host}{final.AbsolutePath})";
        }

        return "returned a web page, not a feed";
    }

    private void RecordFetchResult(string url, FeedFetchResult result)
    {
        if (result.Outcome == FetchOutcome.BackedOff)
        {
            // Nothing reached the origin: the failure that started the backoff
            // already wrote the schedule and the counter; leave the row alone.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        TimeSpan interval;
        DateTimeOffset nextEarliest;

        if (result.Outcome == FetchOutcome.Failed)
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

        // Validators: a successful fetch that returned new ones replaces the
        // stored pair; a 304, a failure or a fetch without validators keeps it.
        var hasNewValidators = result.Outcome == FetchOutcome.Fetched && (result.ETag != null || result.LastModified != null);
        this.persistedFeeds.RecordFetchResult(
            url, now, nextEarliest, interval,
            result.ETag, result.LastModified,
            keepValidators: !hasNewValidators,
            failed: result.Outcome == FetchOutcome.Failed,
            error: result.Error);
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
    /// URL + resolved thumbnail set. The result's outcome tells the caller whether
    /// to store the posts and how to schedule the next fetch.
    /// </summary>
    private async Task<FeedFetchResult> FetchParsedItemsCoreAsync(string url, CancellationToken token)
    {
        if (!EnableHttpLookup)
        {
            return new FeedFetchResult { Outcome = FetchOutcome.NotModified };
        }

        var health = feedHealth.GetOrAdd(url, HealthFromPersisted);

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
        string newEtag = null;
        DateTimeOffset? newLastModified = null;
        string lastError = null;

        try
        {
            foreach (string agent in agents)
            {
                string response = null;
                string nonFeedNote = null;
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
                        lastError = $"HTTP {(int)httpRes.StatusCode}";
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
                        lastError = $"HTTP {(int)httpRes.StatusCode}";
                        this.logger.LogWarning(
                            "Feed {url} returned {status} for agent {agent}.",
                            url, (int)httpRes.StatusCode, agent);
                        continue;
                    }

                    response = await httpRes.Content.ReadAsStringAsync(token);

                    if (string.IsNullOrEmpty(response))
                    {
                        lastError = "empty response";
                        this.logger.LogWarning("Empty response when refreshing feed: {url} (headers: {headers})", url, httpRes.Headers);
                        continue;
                    }

                    nonFeedNote = DescribeNonFeed(httpRes, url, response);

                    // Remember validators so future refreshes can short-circuit with a 304.
                    newEtag = httpRes.Headers.ETag?.ToString();
                    newLastModified = httpRes.Content.Headers.LastModified;

                    var items = this.deserializer.FromString(response, SchedulerUser, url).ToList();
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
                    // A web page where a feed should be (a retired URL redirecting to a
                    // landing page) is the common case; say that, not "'doctype' is an
                    // unexpected token".
                    lastError = TruncateError(nonFeedNote ?? DescribeFailure(ex));
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
                ETag = newEtag,
                LastModified = newLastModified,
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
        return new FeedFetchResult { Outcome = FetchOutcome.Failed, BackoffUntil = until, Error = lastError };
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
    /// The conditional-GET validators stored on the feed row, so a fresh
    /// process still sends a conditional GET and can earn a 304 instead of a
    /// cold fetch after a restart.
    /// </summary>
    private (EntityTagHeaderValue ETag, DateTimeOffset? LastModified) GetValidators(string url)
    {
        FeedFetchState persisted = null;
        try
        {
            persisted = this.persistedFeeds.GetFeedFetchState(url);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to read validators for {url}; fetching unconditionally.", url);
        }

        EntityTagHeaderValue etag = null;
        if (persisted != null && !string.IsNullOrEmpty(persisted.ETag))
        {
            EntityTagHeaderValue.TryParse(persisted.ETag, out etag);
        }

        return (etag, persisted?.LastModified);
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
