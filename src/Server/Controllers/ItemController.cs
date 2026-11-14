using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Config;
using RssReader.Server.Services;
using System.Text;
using System.Threading.Tasks;
using System.Security.Claims;
using Server.Authentication;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ItemController : UserScopedController
    {
        private readonly IItemRepository itemRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IFeedRefresher feedRefresher;
        private readonly RssAppConfig config;
        private readonly ILogger<ItemController> logger;
        private readonly ItemsChangeStamp changeStamp;

        // Upper bound on ids honoured by the batch content endpoint.
        private const int MaxContentBatchSize = 50;

        // How long the browser may reuse a newCount answer without asking. After
        // this it revalidates with If-None-Match, which costs no SQL unless items
        // actually changed (see ItemsChangeStamp).
        private const int NewCountMaxAgeSeconds = 60;

        public ItemController(
            IItemRepository itemRepository,
            IFeedRepository feedRepository,
            IUserResolver userResolver,
            IFeedRefresher feedRefresher,
            RssAppConfig config,
            ILogger<ItemController> logger,
            ItemsChangeStamp changeStamp = null
        )
            : base(userResolver)
        {
            this.changeStamp = changeStamp ?? new ItemsChangeStamp();
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // GET: api/item/timeline
        // The list endpoints carry per-user read/saved flags that change between
        // calls, so the browser must never answer them from its HTTP cache (a
        // back-navigation would otherwise show a post Unread that was just read).
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [HttpGet("timeline")]
        public async Task<IActionResult> TimelineAsync(bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null)
        {
            var user = this.CurrentUser;

            // Enforce both-or-neither cursor params
            long? lastId = null;
            long? lastPublishDateOrder = null;
            if (cursorPublishDateOrder.HasValue && cursorId.HasValue)
            {
                lastId = cursorId;
                lastPublishDateOrder = cursorPublishDateOrder;
            }

            // When no explicit tag filter and not filtering saved items, exclude feeds with hidden tags
            IEnumerable<string> excludeFeedUrls = null;
            if (string.IsNullOrWhiteSpace(filterTag) && !isFilterSaved)
            {
                excludeFeedUrls = this.feedRepository.GetHiddenFeedUrls(user);
            }

            var feed = new NewsFeed("%", user.Id);
            var items = await this.itemRepository.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize, lastId: lastId, lastPublishDateOrder: lastPublishDateOrder, excludeFeedUrls: excludeFeedUrls, includeContent: false);
            var result = items
                .DistinctBy(i => i.Href)
                .Where(i => string.IsNullOrWhiteSpace(filterTag) || (i.FeedTags?.Contains(filterTag) ?? false))
                .ToList();

            return Ok(result);
        }

        // GET: api/item/newCount
        // Cheap "are there new posts?" probe for an already-open timeline. Returns
        // the number of the user's timeline items NEWER than the client's newest
        // currently-loaded item (cursorPublishDateOrder, cursorId), using the same
        // unread/saved/tag/hidden-feed filters as the timeline so the count matches
        // what a top-of-list reload would surface. COUNT-only; fetches no rows.
        [HttpGet("newCount")]
        public async Task<IActionResult> NewCountAsync(long cursorPublishDateOrder, long cursorId, bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null)
        {
            var user = this.CurrentUser;

            // The probe is polled by every open timeline tab. Let the browser cache
            // the answer briefly, then answer revalidations from the in-memory change
            // stamp: while no item has been written since the cached response, the
            // count cannot have moved, so return 304 without touching the database.
            var etag = $"\"{this.changeStamp.Current:x}\"";
            Response.Headers.CacheControl = $"private, max-age={NewCountMaxAgeSeconds}";
            Response.Headers.ETag = etag;
            if (Request.Headers.IfNoneMatch.Count > 0
                && Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), etag, StringComparison.Ordinal)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            // When no explicit tag filter and not filtering saved items, exclude feeds with hidden tags
            IEnumerable<string> excludeFeedUrls = null;
            if (string.IsNullOrWhiteSpace(filterTag) && !isFilterSaved)
            {
                excludeFeedUrls = this.feedRepository.GetHiddenFeedUrls(user);
            }

            var feed = new NewsFeed("%", user.Id);
            var count = await this.itemRepository.GetNewItemCountAsync(feed, isFilterUnread, isFilterSaved, filterTag, cursorPublishDateOrder, cursorId, excludeFeedUrls: excludeFeedUrls);

            return Ok(count);
        }

        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [HttpGet("feed")]
        public async Task<IActionResult> FeedAsync(string href, bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20, long? cursorPublishDateOrder = null, long? cursorId = null)
        {
            var user = this.CurrentUser;

            var feed = this.feedRepository.GetFeed(user, href);

            if (feed == null)
            {
                return NotFound($"Feed was not found.");
            }

            // Enforce both-or-neither cursor params
            long? lastId = null;
            long? lastPublishDateOrder = null;
            if (cursorPublishDateOrder.HasValue && cursorId.HasValue)
            {
                lastId = cursorId;
                lastPublishDateOrder = cursorPublishDateOrder;
            }

            var items = await this.itemRepository.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize, lastId: lastId, lastPublishDateOrder: lastPublishDateOrder, includeContent: false);
            var result = items
                .DistinctBy(i => i.Href)
                .Where(i => string.IsNullOrWhiteSpace(filterTag) || (i.FeedTags?.Contains(filterTag) ?? false))
                .ToList();

            return Ok(result);
        }

        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [HttpGet("search")]
        public async Task<IActionResult> SearchAsync(string query, int page = 0, int pageSize = 20)
        {
            var user = this.CurrentUser;

            if (query == null)
            {
                return BadRequest("query is required.");
            }

            var items = await this.itemRepository.SearchItemsAsync(query, user, page, pageSize);

            return Ok(items);
        }

        // GET: api/item/content
        [HttpGet("content")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
        public IActionResult GetItemContent(int itemId)
        {
            var user = this.CurrentUser;

            var item = this.itemRepository.GetItem(user, itemId);

            if (item == null)
            {
                return NotFound($"Item not found.");
            }

            var content = this.itemRepository.GetItemContent(item);
            if (string.IsNullOrWhiteSpace(content))
            {
                return NotFound("Content not found for the specified item.");
            }

            var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            base64Content = System.Text.Json.JsonSerializer.Serialize(base64Content);

            return Ok(base64Content);
        }

        // Batch sibling of GET content. The client prefetches the content for a
        // page of timeline items so a later cold start can serve them from its own
        // cache; doing that one id at a time would be one request per post. Items
        // with no stored content are omitted rather than failing the whole batch,
        // so a single empty post can't sink the prefetch.
        [HttpGet("contentBatch")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
        public IActionResult GetItemContentBatch(string itemIds)
        {
            var user = this.CurrentUser;

            if (string.IsNullOrWhiteSpace(itemIds))
            {
                return Ok(new Dictionary<string, string>());
            }

            // Bounded so a crafted query can't turn one request into an unbounded
            // scan; the client only ever prefetches a page's worth.
            var ids = itemIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(raw => int.TryParse(raw, out var parsed) ? parsed : -1)
                .Where(id => id > 0)
                .Distinct()
                .Take(MaxContentBatchSize)
                .ToList();

            var results = new Dictionary<string, string>(ids.Count);

            foreach (var id in ids)
            {
                // GetItem is user-scoped, so ids belonging to another user resolve
                // to null and are skipped rather than leaking content.
                var item = this.itemRepository.GetItem(user, id);
                if (item == null)
                {
                    continue;
                }

                var content = this.itemRepository.GetItemContent(item);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                results[id.ToString()] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            }

            return Ok(results);
        }

        // POST only: a GET that changes state can be triggered cross-site by an
        // image tag with the session cookie attached (no CORS preflight).
        [HttpPost("markAsRead")]
        public IActionResult MarkAsRead(int itemId, bool isRead)
        {
            var user = this.CurrentUser;

            var item = this.itemRepository.GetItem(user, itemId);

            if (item == null)
            {
                return NotFound($"Item not found.");
            }

            this.itemRepository.MarkAsRead(item, isRead, user);
            return Ok();
        }

        [HttpPost("save")]
        public IActionResult SavePost([FromBody] NewsFeedItem item)
        {
            if (item == null)
            {
                return BadRequest("Item is required.");
            }

            if (!this.IsCaller(item.UserId))
            {
                return StatusCode(403, "You can only save items to your own account.");
            }

            var owned = this.FindVisibleItem(item);
            if (owned == null)
            {
                return NotFound("Item not found.");
            }

            this.itemRepository.SavePost(owned, this.CurrentUser);
            return Ok();
        }

        [HttpPost("unsave")]
        public IActionResult UnsavePost([FromBody] NewsFeedItem item)
        {
            if (item == null)
            {
                return BadRequest("Item is required.");
            }

            if (!this.IsCaller(item.UserId))
            {
                return StatusCode(403, "You can only unsave items from your own account.");
            }

            var owned = this.FindVisibleItem(item);
            if (owned == null)
            {
                return NotFound("Item not found.");
            }

            this.itemRepository.UnsavePost(owned, this.CurrentUser);
            return Ok();
        }

        // The body of save/unsave is the client's copy of a post. Only its id (or,
        // failing that, its href) is taken from it, and only a post the caller can
        // already see resolves: saving is what makes a post visible, so trusting
        // the body would let anyone read any feed by saving its posts by id.
        private NewsFeedItem FindVisibleItem(NewsFeedItem item)
        {
            NewsFeedItem found = null;
            if (int.TryParse(item.Id, out var id) && id > 0)
            {
                found = this.itemRepository.GetItem(this.CurrentUser, id);
            }
            if (found == null && !string.IsNullOrEmpty(item.Href))
            {
                found = this.itemRepository.GetItem(this.CurrentUser, item.Href);
            }
            return found;
        }

        [HttpDelete("all")]
        public async Task<IActionResult> DeleteAllItemsAsync()
        {
            if (!this.config.IsTestUserEnabled)
            {
                return StatusCode(403, "This endpoint is only available in test mode.");
            }

            var user = this.CurrentUser;

            await this.itemRepository.DeleteAllItemsAsync(user);
            this.feedRefresher.ResetRefreshCooldown();
            return Ok();
        }
    }
}