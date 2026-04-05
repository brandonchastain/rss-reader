using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Config;
using System.Text;
using System.Threading.Tasks;
using System.Security.Claims;


namespace Server.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ItemController : ControllerBase
    {
        private readonly IItemRepository itemRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IUserRepository userRepository;
        private readonly IUserResolver userResolver;
        private readonly IFeedRefresher feedRefresher;
        private readonly RssAppConfig config;
        private readonly ILogger<ItemController> logger;

        public ItemController(
            IItemRepository itemRepository,
            IFeedRepository feedRepository,
            IUserRepository userRepository,
            IUserResolver userResolver,
            IFeedRefresher feedRefresher,
            RssAppConfig config,
            ILogger<ItemController> logger
        )
        {
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.userResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // GET: api/item/timeline
        [HttpGet("timeline")]
        public async Task<IActionResult> TimelineAsync(bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20)
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

            // When no explicit tag filter and not filtering saved items, exclude feeds with hidden tags
            IEnumerable<string> excludeFeedUrls = null;
            if (string.IsNullOrWhiteSpace(filterTag) && !isFilterSaved)
            {
                excludeFeedUrls = this.feedRepository.GetHiddenFeedUrls(user);
            }

            // TODO: authenticate the real user
            var feed = new NewsFeed("%", user.Id);
            var items = await this.itemRepository.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize, excludeFeedUrls: excludeFeedUrls);
            var result = items
                .DistinctBy(i => i.Href)
                .OrderByDescending(i => i.PublishDateOrder)
                .Where(i => string.IsNullOrWhiteSpace(filterTag) || (i.FeedTags?.Contains(filterTag) ?? false))
                .ToHashSet();

            return Ok(result);
        }

        [HttpGet("feed")]
        public async Task<IActionResult> FeedAsync(string href, bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20)
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

            var feed = this.feedRepository.GetFeed(user, href);

            if (feed == null)
            {
                return NotFound($"Feed was not found.");
            }

            var items = await this.itemRepository.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize);
            var result = items
                .DistinctBy(i => i.Href)
                .OrderByDescending(i => i.PublishDateOrder)
                .Where(i => string.IsNullOrWhiteSpace(filterTag) || (i.FeedTags?.Contains(filterTag) ?? false))
                .ToHashSet();

            return Ok(result);
        }

        [HttpGet("search")]
        public async Task<IActionResult> SearchAsync(string query, int page = 0, int pageSize = 20)
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

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
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

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

        [HttpGet("markAsRead")]
        public IActionResult MarkAsRead(int itemId, bool isRead)
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

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

            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (item.UserId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only save items to your own account.");
            }

            var user = this.userRepository.GetUserById(item.UserId);
            if (user == null)
            {
                return NotFound($"User not found.");
            }

            this.itemRepository.SavePost(item, user);
            return Ok();
        }

        [HttpPost("unsave")]
        public IActionResult UnsavePost([FromBody] NewsFeedItem item)
        {
            if (item == null)
            {
                return BadRequest("Item is required.");
            }

            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (item.UserId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only unsave items from your own account.");
            }

            var user = this.userRepository.GetUserById(item.UserId);
            if (user == null)
            {
                return NotFound($"User not found.");
            }

            this.itemRepository.UnsavePost(item, user);
            return Ok();
        }

        [HttpDelete("all")]
        public async Task<IActionResult> DeleteAllItemsAsync()
        {
            if (!this.config.IsTestUserEnabled)
            {
                return StatusCode(403, "This endpoint is only available in test mode.");
            }

            var user = this.userResolver.ResolveUser(User);
            if (user == null)
            {
                return NotFound("Authenticated user not found.");
            }

            await this.itemRepository.DeleteAllItemsAsync(user);
            this.feedRefresher.ResetRefreshCooldown();
            return Ok();
        }
    }
}