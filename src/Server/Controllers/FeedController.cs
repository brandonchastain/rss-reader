using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Serialization;
using RssReader.Server.Services;
using RssApp.RssClient;
using System.Runtime.InteropServices;
using Server.Authentication;

namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FeedController : UserScopedController
    {
        // Minimum followers for a feed to be listed as popular. 1 = a plain top list;
        // raise it as the user base grows so a lone user's feed is not listed.
        private const int PopularFeedMinSubscribers = 1;

        private readonly IFeedRepository feedRepository;
        private readonly IFeedRefresher feedRefresher;
        private readonly IItemRepository itemRepository;
        private readonly FaviconService faviconService;
        private readonly OutboundUrlGuard urlGuard;
        private readonly ILogger<UserController> logger;

        public FeedController(
            IFeedRepository feedRepository,
            IFeedRefresher feedRefresher,
            IItemRepository itemRepository,
            IUserResolver userResolver,
            FaviconService faviconService,
            OutboundUrlGuard urlGuard,
            ILogger<UserController> logger)
            : base(userResolver)
        {
            this.urlGuard = urlGuard ?? throw new ArgumentNullException(nameof(urlGuard));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.feedRefresher = feedRefresher ?? throw new ArgumentNullException(nameof(feedRefresher));
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.faviconService = faviconService ?? throw new ArgumentNullException(nameof(faviconService));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // Serves a site favicon from our own origin (cached server-side) so the
        // browser never contacts a third-party icon service directly. Anonymous:
        // favicons aren't user data, and <img> loads shouldn't depend on auth.
        [HttpGet("icon")]
        [AllowAnonymous]
        public async Task<IActionResult> GetIconAsync([FromQuery] string domain)
        {
            var path = await this.faviconService.GetFaviconPathAsync(domain);
            if (path == null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "public, max-age=604800";
            return PhysicalFile(path, "image/x-icon");
        }

        [HttpPost]
        [Route("importOpml")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Server.Authentication.RateLimits.AddFeedPolicy)]
        public async Task<IActionResult> ImportOpmlAsync(OpmlImport import)
        {
            await Task.Yield();
            if (!this.IsCaller(import.UserId))
            {
                return StatusCode(403, "You can only import OPML for your own account.");
            }

            var user = this.CurrentUser;
            var feeds = OpmlSerializer.ParseOpmlContent(import.OpmlContent, user.Id);
            this.feedRepository.AddFeeds(user, feeds);
            return Ok();
        }

        [HttpGet]
        [Route("exportOpml")]
        public async Task<IActionResult> ExportOpmlAsync(int userId)
        {
            await Task.Yield();
            if (!this.IsCaller(userId))
            {
                return StatusCode(403, "You can only export OPML for your own account.");
            }

            var feeds = this.feedRepository.GetFeeds(this.CurrentUser);
            var fileContent = OpmlSerializer.GenerateOpmlContent(feeds);

            return Ok(fileContent);
        }

        [HttpGet]
        [Route("refresh")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> RefreshFeedsAsync()
        {
            var user = this.CurrentUser;

            // Fire-and-forget: enqueue work and return immediately.
            await this.feedRefresher.RefreshAsync(user);

            return Accepted();
        }

        [HttpGet]
        [Route("refresh/status")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public IActionResult GetRefreshStatus()
        {
            var user = this.CurrentUser;

            var status = this.feedRefresher.GetRefreshStatus(user);
            return Ok(status);
        }

        [HttpPost]
        [Route("tags")]
        public async Task<IActionResult> AddTagAsync([FromBody] NewsFeed feed)
        {
            if (feed == null || string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed data is required.");
            }

            if (!this.IsCaller(feed.UserId))
            {
                return StatusCode(403, "You can only add tags to your own feeds.");
            }

            var user = this.CurrentUser;
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


        [HttpPost]
        [Route("tags/remove")]
        public IActionResult RemoveTagAsync([FromBody] NewsFeed feed)
        {
            if (feed == null || string.IsNullOrWhiteSpace(feed.Href) || feed.UserId <= 0)
            {
                return BadRequest("Feed data is required.");
            }

            if (!this.IsCaller(feed.UserId))
            {
                return StatusCode(403, "You can only remove tags from your own feeds.");
            }

            var existingFeed = this.feedRepository.GetFeed(this.CurrentUser, feed.Href);
            if (existingFeed == null)
            {
                return NotFound($"Feed with URL '{feed.Href}' not found.");
            }

            foreach (var tag in (feed.Tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
            {
                this.feedRepository.RemoveTag(existingFeed, tag);
            }

            return Ok();
        }

        [HttpGet]
        [Route("tags")]
        public async Task<IActionResult> GetUserTagsAsync(int userId)
        {
            await Task.Yield();
            if (!this.IsCaller(userId))
            {
                return StatusCode(403, "You can only access your own tags.");
            }

            var tags = this.feedRepository.GetFeeds(this.CurrentUser)
                .SelectMany(f => f.Tags)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct();

            return Ok(tags);
        }

        [HttpGet]
        [Route("tagSettings")]
        public IActionResult GetTagSettings()
        {
            var user = this.CurrentUser;

            var settings = this.feedRepository.GetTagSettings(user);
            return Ok(settings);
        }

        [HttpPut]
        [Route("tagSettings")]
        public IActionResult SetTagHidden([FromBody] TagSetting setting)
        {
            if (setting == null || string.IsNullOrWhiteSpace(setting.Tag))
            {
                return BadRequest("Tag setting is required.");
            }

            var user = this.CurrentUser;

            this.feedRepository.SetTagHidden(user, setting.Tag, setting.IsHidden);
            var settings = this.feedRepository.GetTagSettings(user);
            return Ok(settings);
        }

        // GET: api/feed/popular
        // The most-subscribed feeds overall (the page marks the ones the caller follows).
        [HttpGet]
        [Route("popular")]
        public IActionResult GetPopularFeeds([FromQuery] int limit = 10)
        {
            var user = this.CurrentUser;

            limit = Math.Clamp(limit, 1, 50);
            return Ok(this.feedRepository.GetPopularFeeds(limit, PopularFeedMinSubscribers));
        }

        // GET: api/feed
        [HttpGet]
        public IActionResult GetFeeds()
        {
            var user = this.CurrentUser;

            var feeds = this.feedRepository.GetFeeds(user);
            if (feeds == null)
            {
                feeds = new List<NewsFeed>();
            }

            return Ok(feeds);
        }

        // POST: api/feed
        [HttpPost]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(Server.Authentication.RateLimits.AddFeedPolicy)]
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

            if (!this.IsCaller(feed.UserId))
            {
                return StatusCode(403, "You can only add feeds to your own account.");
            }

            // Reject non-public destinations before the feed is stored, so the
            // user sees why instead of a feed that silently fails every refresh.
            // The fetch pipeline re-checks every request (and redirect) anyway.
            if (!Uri.TryCreate(feed.Href, UriKind.Absolute, out var feedUri))
            {
                return BadRequest("Feed URL must be an absolute http or https URL.");
            }
            try
            {
                await this.urlGuard.ValidateAsync(feedUri, HttpContext.RequestAborted);
            }
            catch (BlockedOutboundUrlException ex)
            {
                this.logger.LogWarning("Rejected feed URL {Href} for user {UserId}: {Reason}", feed.Href, feed.UserId, ex.Message);
                return BadRequest(ex.Message);
            }

            try
            {

                // Subscribe. The feed row is shared: if it already exists its
                // stored posts are visible to the new subscriber at once and no
                // fetch happens; a brand-new row is fetched once, now, so the
                // first subscriber does not wait for the scheduler.
                var user = this.CurrentUser;
                var existingFeed = this.feedRepository.GetFeed(user, feed.Href);
                if (existingFeed == null)
                {
                    this.feedRepository.AddFeed(feed);
                    await this.feedRefresher.FetchNewFeedAsync(feed.Href);
                }

                return CreatedAtAction(nameof(GetFeeds), new { username = user.Username }, feed);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error adding feed.");
                return StatusCode(500, "Internal server error while adding feed.");
            }
        }

        // DELETE, not POST: this binds from the query string, and a cross-site
        // HTML form can POST without a CORS preflight; DELETE always preflights.
        [HttpDelete]
        [Route("delete")]
        public async Task<IActionResult> DeleteFeedAsync([FromQuery]string href)
        {
            await Task.Yield();
            var user = this.CurrentUser;

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