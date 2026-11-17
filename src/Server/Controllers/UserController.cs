using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
using RssApp.Config;
using System.Threading.Tasks;
using System.Security.Claims;
using Server.Authentication;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : UserScopedController
    {
        private readonly IUserRepository userRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IItemRepository itemRepository;
        private readonly ILogger<UserController> logger;
        private readonly RssAppConfig config;

        public UserController(
            IUserRepository userRepository,
            IFeedRepository feedRepository,
            IItemRepository itemRepository,
            IUserResolver userResolver,
            ILogger<UserController> logger,
            RssAppConfig config)
            : base(userResolver)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // GET: api/user
        [HttpGet]
        public IActionResult GetUserByName()
        {
            var user = this.CurrentUser;

            user.IsAdmin = Server.Authentication.AdminPolicy.IsAdmin(config, User, user);
            return Ok(user);
        }

        // GET: api/user/report
        [HttpGet("report")]
        public IActionResult GetDataReport()
        {
            var user = this.CurrentUser;

            var feeds = feedRepository.GetFeeds(user).ToList();
            var feedSummaries = feeds.Select(f => new FeedSummary
            {
                Url = f.Href,
                Tags = f.Tags?.ToList() ?? new List<string>(),
                ItemCount = itemRepository.GetItemCountForFeed(user, f.Href)
            }).ToList();

            var report = new UserDataReport
            {
                Username = user.Username,
                FeedCount = feeds.Count,
                TotalItemCount = feedSummaries.Sum(f => f.ItemCount),
                Feeds = feedSummaries
            };

            return Ok(report);
        }

        // DELETE: api/user
        // An admin can never delete their own account, so the admin dashboard
        // cannot be locked out from the Account page.
        [HttpDelete]
        public async Task<IActionResult> DeleteAccountAsync()
        {
            var user = this.CurrentUser;

            if (Server.Authentication.AdminPolicy.IsAdmin(config, User, user))
            {
                logger.LogWarning("Refused self-delete for admin user {UserId}", user.Id);
                return StatusCode(StatusCodes.Status403Forbidden, "Admin accounts cannot be deleted.");
            }

            // Cascade: items+content → feeds → user
            await itemRepository.DeleteAllItemsAsync(user);
            feedRepository.DeleteAllFeeds(user);
            userRepository.DeleteUser(user.Id);

            // End the session too, so the next page load shows sign-in
            // instead of a session with no account behind it.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            logger.LogWarning("User {UserId} deleted their account", user.Id);
            return NoContent();
        }
    }
}
