using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RssApp.Data;
using RssApp.Contracts;
using RssApp.ComponentServices;
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
        private readonly IUserResolver userResolver;
        private readonly ILogger<UserController> logger;
        private readonly SemaphoreSlim locker = new SemaphoreSlim(1, 1);

        public UserController(
            IUserRepository userRepository,
            IUserResolver userResolver,
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.userResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            return Ok(user);
        }

        // POST: api/user/register
        [HttpPost("register")]
        public async Task<IActionResult> RegisterAsync()
        {
            var aadId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = User.FindFirst(ClaimTypes.Name)?.Value;

            if (string.IsNullOrEmpty(aadId) && string.IsNullOrEmpty(email))
            {
                return Unauthorized("User is not authenticated.");
            }

            await locker.WaitAsync();
            try
            {
                var existing = this.userResolver.ResolveUser(User);
                if (existing != null)
                {
                    return Ok(existing);
                }

                var newUser = this.userResolver.ResolveOrCreateUser(User);
                return Created(nameof(RegisterAsync), newUser);
            }
            finally
            {
                locker.Release();
            }
        }
    }
}
