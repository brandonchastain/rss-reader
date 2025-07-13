using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;


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
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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