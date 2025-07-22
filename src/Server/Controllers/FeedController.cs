using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Serialization;
using System.Runtime.InteropServices;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IFeedRefresher feedRefresher;
        private readonly IItemRepository itemRepository;
        private readonly ILogger<UserController> logger;

        public FeedController(
            IUserRepository userRepository,
            IFeedRepository feedRepository,
            IFeedRefresher feedRefresher,
            IItemRepository itemRepository,
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost]
        [Route("importOpml")]
        public async Task<IActionResult> ImportOpml(OpmlImport import)
        {
            var userId = import.UserId;
            var opmlContent = import.OpmlContent;
            var user = this.userRepository.GetUserById(userId);
            if (user == null)
            {
                return NotFound($"User with ID '{userId}' not found.");
            }

            var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, userId);
            this.feedRepository.ImportFeeds(user, feeds);
            return Ok();
        }

        [HttpGet]
        [Route("refresh")]
        public async Task<IActionResult> RefreshFeeds(string username)
        {
            if (username == null)
            {
                return BadRequest("username is required.");
            }

            var user = this.userRepository.GetUserByName(username);
            await this.feedRefresher.RefreshAsync(user);
            return Ok();
        }

        //            await _httpClient.PostAsJsonAsync($"{_config.ApiBaseUrl}/api/feed/tags", feed);
        [HttpPost]
        [Route("tags")]
        public async Task<IActionResult> AddTag([FromBody] NewsFeed feed)
        {
            if (feed == null || string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed data is required.");
            }

            var user = this.userRepository.GetUserById(feed.UserId);
            if (user == null)
            {
                return NotFound($"User with ID '{feed.UserId}' not found.");
            }

            var existingFeed = this.feedRepository.GetFeed(user, feed.Href);
            if (existingFeed == null)
            {
                return NotFound($"Feed with URL '{feed.Href}' not found for user '{user.Username}'.");
            }

            var existingTags = existingFeed.Tags ?? new List<string>();
            var newTags = feed.Tags?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct()
                .Except(existingTags)
                .ToList() ?? new List<string>();

            var items = await this.itemRepository.GetItemsAsync(existingFeed, false, false, null, 0, 100);

            foreach (var tag in newTags)
            {
                this.feedRepository.AddTag(existingFeed, tag);
            }

            foreach (var item in items)
            {
                string tags = string.Join(",", newTags.Union(item.FeedTags ?? new List<string>()));
                this.itemRepository.UpdateTags(item, tags);
            }

            return Ok();
        }


        [HttpGet]
        [Route("tags")]
        public async Task<IActionResult> GetUserTags(int userId)
        {
            var user = this.userRepository.GetUserById(userId);
            if (user == null)
            {
                return NotFound($"User with ID '{userId}' not found.");
            }

            var tags = this.feedRepository.GetFeeds(user)
                .SelectMany(f => f.Tags)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct();

            return Ok(tags);
        }

        // GET: api/feed
        [HttpGet]
        public IActionResult GetFeeds(string username)
        {
            if (username == null)
            {
                return BadRequest("Username is required.");
            }

            var user = this.userRepository.GetUserByName(username);
            if (user == null)
            {
                return NotFound($"User '{username}' not found.");
            }

            var feeds = this.feedRepository.GetFeeds(user);
            if (feeds == null)
            {
                feeds = new List<NewsFeed>();
            }

            return Ok(feeds);
        }

        // POST: api/feed
        [HttpPost]
        public async Task<IActionResult> AddFeed([FromBody] NewsFeed feed)
        {
            if (feed == null)
            {
                return BadRequest("Feed data is required.");
            }
            if (string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed URL and User ID are required.");
            }

            try
            {

                var user = this.userRepository.GetUserById(feed.UserId);
                var existingFeed = this.feedRepository.GetFeed(user, feed.Href);
                if (existingFeed == null)
                {
                    this.feedRepository.AddFeed(feed);
                    await this.feedRefresher.AddFeedAsync(feed);
                }

                return CreatedAtAction(nameof(GetFeeds), new { username = this.userRepository.GetUserById(feed.UserId)?.Username }, feed);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error adding feed.");
                return StatusCode(500, "Internal server error while adding feed.");
            }
        }

        [HttpPost]
        [Route("delete")]
        public async Task<IActionResult> DeleteFeed([FromQuery]string username, [FromQuery]string href)
        {
            if (username == null || href == null)
            {
                return BadRequest("Username and feed URL are required.");
            }

            var user = this.userRepository.GetUserByName(username);
            if (user == null)
            {
                return NotFound($"User '{username}' not found.");
            }

            var feed = this.feedRepository.GetFeed(user, href);
            if (feed == null)
            {
                return NotFound($"Feed with URL '{href}' not found for user '{username}'.");
            }

            this.feedRepository.DeleteFeed(user, feed.Href);
            return Ok();
        }
    }
}