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


namespace Server.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly IFeedRepository feedRepository;
        private readonly IItemRepository itemRepository;
        private readonly IUserResolver userResolver;
        private readonly ILogger<UserController> logger;
        private readonly RssAppConfig config;
        private readonly SemaphoreSlim locker = new SemaphoreSlim(1, 1);

        public UserController(
            IUserRepository userRepository,
            IFeedRepository feedRepository,
            IItemRepository itemRepository,
            IUserResolver userResolver,
            ILogger<UserController> logger,
            RssAppConfig config)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.feedRepository = feedRepository ?? throw new ArgumentNullException(nameof(feedRepository));
            this.itemRepository = itemRepository ?? throw new ArgumentNullException(nameof(itemRepository));
            this.userResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
        }

        // GET: api/user
        [HttpGet]
        public IActionResult GetUserByName()
        {
            var user = this.userResolver.ResolveUser(User);

            if (user == null)
            {
                return NotFound();
            }

            user.IsAdmin = Server.Authentication.AdminPolicy.IsAdmin(config, User, user);
            return Ok(user);
        }

        // POST: api/user/register
        [HttpPost("register")]
        public async Task<IActionResult> RegisterAsync()
        {
            var aadId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(aadId))
            {
                return Unauthorized("User is not authenticated.");
            }

            await locker.WaitAsync();
            try
            {
                var existing = this.userResolver.ResolveUser(User);
                if (existing != null)
                {
                    existing.IsAdmin = Server.Authentication.AdminPolicy.IsAdmin(config, User, existing);
                    return Ok(existing);
                }

                var newUser = this.userResolver.ResolveOrCreateUser(User);
                newUser.IsAdmin = Server.Authentication.AdminPolicy.IsAdmin(config, User, newUser);
                return Created(nameof(RegisterAsync), newUser);
            }
            finally
            {
                locker.Release();
            }
        }

        // GET: api/user/report
        [HttpGet("report")]
        public IActionResult GetDataReport()
        {
            var user = userResolver.ResolveUser(User);
            if (user == null) return NotFound();

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
        public async Task<IActionResult> DeleteAccount()
        {
            var user = userResolver.ResolveUser(User);
            if (user == null) return NotFound();

            if (Server.Authentication.AdminPolicy.IsAdmin(config, User, user))
            {
                logger.LogWarning("Refused self-delete for admin user {UserId}", user.Id);
                return StatusCode(StatusCodes.Status403Forbidden, "Admin accounts cannot be deleted.");
            }

            // Cascade: items+content → feeds → user
            await itemRepository.DeleteAllItemsAsync(user);
            feedRepository.DeleteAllFeeds(user);
            userRepository.DeleteUser(user.Id);

            // End the session too. A still-valid cookie makes the next page
            // load call register, which silently creates a fresh account for
            // the same login, so the delete looked like it never happened.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            logger.LogWarning("User {UserId} deleted their account", user.Id);
            return NoContent();
        }
    }
}
