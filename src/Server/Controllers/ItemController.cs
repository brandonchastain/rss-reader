using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;
using System.Text;
using System.Threading.Tasks;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ItemController : ControllerBase
    {
        private readonly IItemRepository itemRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IUserRepository userRepository;
        private readonly ILogger<ItemController> logger;

        public ItemController(
            IItemRepository itemRepository,
            IFeedRepository feedRepository,
            IUserRepository userRepository,
            ILogger<ItemController> logger)
        {
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // GET: api/item/timeline
        [HttpGet("timeline")]
        public async Task<IActionResult> TimelineAsync(string username, bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            var user = this.userRepository.GetUserByName(username);

            // TODO: authenticate the real user
            var feed = new NewsFeed("%", user.Id);
            var items = await this.itemRepository.GetItemsAsync(feed, isFilterUnread, isFilterSaved, filterTag, page, pageSize);
            var result = items
                .DistinctBy(i => i.Href)
                .OrderByDescending(i => i.PublishDateOrder)
                .Where(i => string.IsNullOrWhiteSpace(filterTag) || (i.FeedTags?.Contains(filterTag) ?? false))
                .ToHashSet();

            return Ok(result);
        }

        // GET: api/item/feed/?username={}href={feedUrl}&isFilterUnread={isFilterUnread}&isFilterSaved={isFilterSaved}&filterTag={filterTag}&page={page}&pageSize={pageSize}
        [HttpGet("feed")]
        public async Task<IActionResult> FeedAsync(string username, string href, bool isFilterUnread = false, bool isFilterSaved = false, string filterTag = null, int page = 0, int pageSize = 20)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            var user = this.userRepository.GetUserByName(username);
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

        // GET: api/item/search/?username={username}&query={query}&isFilterUnread={isFilterUnread}&isFilterSaved={isFilterSaved}&filterTag={filterTag}&page={page}&pageSize={pageSize}
        [HttpGet("search")]
        public async Task<IActionResult> SearchAsync(string username, string query, int page = 0, int pageSize = 20)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            if (query == null)
            {
                return BadRequest("query is required.");
            }

            var user = this.userRepository.GetUserByName(username);
            var items = await this.itemRepository.SearchItemsAsync(query, user, page, pageSize);

            return Ok(items);
        }

        // GET: api/item/content
        [HttpGet("content")]
        public IActionResult GetItemContent(string username, int itemId)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            var user = this.userRepository.GetUserByName(username);
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

            // encode content into base64
            var base64Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            base64Content = System.Text.Json.JsonSerializer.Serialize(base64Content);

            return Ok(base64Content);
        }

        [HttpGet("markAsRead")]
        public IActionResult MarkAsRead(string username, int itemId)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            var user = this.userRepository.GetUserByName(username);
            var item = this.itemRepository.GetItem(user, itemId);

            if (item == null)
            {
                return NotFound($"Item not found.");
            }


            this.itemRepository.MarkAsRead(item, !item.IsRead);
            return Ok();
        }

        [HttpPost("save")]
        public IActionResult SavePost([FromBody] NewsFeedItem item)
        {
            if (item == null)
            {
                return BadRequest("Item is required.");
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

            var user = this.userRepository.GetUserById(item.UserId);
            if (user == null)
            {
                return NotFound($"User not found.");
            }

            this.itemRepository.UnsavePost(item, user);
            return Ok();
        }
    }
}
