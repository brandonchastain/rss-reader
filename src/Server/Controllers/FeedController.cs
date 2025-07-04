using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly IFeedRepository feedRepository;
        private readonly ILogger<UserController> logger;

        public FeedController(
            IUserRepository userRepository,
            IFeedRepository feedRepository,
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            if (feeds == null || !feeds.Any())
            {
                return NotFound($"No feeds found for user '{username}'.");
            }

            return Ok(feeds);
        }

        // POST: api/feed
        [HttpPost]
        public IActionResult AddFeed([FromBody] NewsFeed feed)
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
                this.feedRepository.AddFeed(feed);
                return CreatedAtAction(nameof(GetFeeds), new { username = this.userRepository.GetUserById(feed.UserId)?.Username }, feed);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error adding feed.");
                return StatusCode(500, "Internal server error while adding feed.");
            }
        }
    }
}