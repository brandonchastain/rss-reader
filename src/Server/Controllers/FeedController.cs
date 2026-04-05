using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Serialization;
using System.Runtime.InteropServices;
using System.Security.Claims;


namespace Server.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class FeedController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IFeedRefresher feedRefresher;
        private readonly IItemRepository itemRepository;
        private readonly IUserResolver userResolver;
        private readonly ILogger<UserController> logger;

        public FeedController(
            IUserRepository userRepository,
            IFeedRepository feedRepository,
            IFeedRefresher feedRefresher,
            IItemRepository itemRepository,
            IUserResolver userResolver,
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.userResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost]
        [Route("importOpml")]
        public async Task<IActionResult> ImportOpmlAsync(OpmlImport import)
        {
            await Task.Yield();
            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (import.UserId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only import OPML for your own account.");
            }

            var userId = import.UserId;
            var opmlContent = import.OpmlContent;
            var user = this.userRepository.GetUserById(userId);
            if (user == null)
            {
                return NotFound($"User with ID '{userId}' not found.");
            }

            var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, userId);
            this.feedRepository.AddFeeds(user, feeds);
            return Ok();
        }

        [HttpGet]
        [Route("exportOpml")]
        public async Task<IActionResult> ExportOpmlAsync(int userId)
        {
            await Task.Yield();
            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (userId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only export OPML for your own account.");
            }

            var user = this.userRepository.GetUserById(userId);

            if (user == null)
            {
                return NotFound($"User with ID '{userId}' not found.");
            }

            var feeds = this.feedRepository.GetFeeds(user);
            var fileContent = OpmlSerializer.GenerateOpmlContent(feeds);

            return Ok(fileContent);
        }

        [HttpGet]
        [Route("refresh")]
        public async Task<IActionResult> RefreshFeedsAsync()
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            await this.feedRefresher.RefreshAsync(user);

            return Accepted();
        }

        [HttpGet]
        [Route("refresh/status")]
        public async Task<IActionResult> RefreshStatusAsync()
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            bool hasNewItems = await this.feedRefresher.HasNewItemsAsync(user);
            if (hasNewItems)
            {
                return Ok();
            }

            return NoContent();
        }

        [HttpPost]
        [Route("tags")]
        public async Task<IActionResult> AddTagAsync([FromBody] NewsFeed feed)
        {
            if (feed == null || string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed data is required.");
            }

            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (feed.UserId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only add tags to your own feeds.");
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
                .Select(t => t.Trim())
                .Select(t => t.ToLowerInvariant())
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
        public async Task<IActionResult> GetUserTagsAsync(int userId)
        {
            await Task.Yield();
            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (userId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only access your own tags.");
            }

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
        public IActionResult GetFeeds()
        {
            var user = this.userResolver.ResolveUser(User);
            if (user == null)
            {
                return NotFound($"Authenticated user not found.");
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
        public async Task<IActionResult> AddFeedAsync([FromBody] NewsFeed feed)
        {
            if (feed == null)
            {
                return BadRequest("Feed data is required.");
            }
            if (string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed URL and User ID are required.");
            }

            var authenticatedUser = this.userResolver.ResolveUser(User);
            if (authenticatedUser == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (feed.UserId != authenticatedUser.Id)
            {
                return StatusCode(403, "You can only add feeds to your own account.");
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
        public async Task<IActionResult> DeleteFeedAsync([FromQuery]string href)
        {
            await Task.Yield();
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound($"Authenticated user not found.");
            }

            if (href == null)
            {
                return BadRequest("feed URL is required.");
            }

            var feed = this.feedRepository.GetFeed(user, href);
            if (feed == null)
            {
                return NotFound($"Feed with URL '{href}' not found.");
            }

            this.feedRepository.DeleteFeed(user, feed.Href);
            return Ok();
        }
    }
}