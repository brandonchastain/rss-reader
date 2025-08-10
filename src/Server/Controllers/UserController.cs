using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;
using System.Threading.Tasks;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly ILogger<UserController> logger;
        private readonly SemaphoreSlim locker = new SemaphoreSlim(1, 1);

        public UserController(
            IUserRepository userRepository,
            ILogger<UserController> logger)
        {
            this.userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // GET: api/user
        [HttpGet]
        public IActionResult GetUserByName(string username)
        {
            var user = this.userRepository.GetUserByName(username);

            if (user == null)
            {
                return NotFound();
            }

            return Ok(user);
        }

        // GET: api/user/{id}
        [HttpGet("{id}")]
        public IActionResult GetUserById(int id)
        {
            var user = this.userRepository.GetUserById(id);

            if (user == null)
            {
                return NotFound();
            }

            return Ok(user);
        }

        // POST: api/user/register
        [HttpPost("register")]
        public async Task<IActionResult> RegisterAsync([FromBody] string username)
        {
            await locker.WaitAsync();
            try
            {
                var user = this.userRepository.GetUserByName(username);
                if (user != null)
                {
                    return Ok(user);
                }

                var newUser = this.userRepository.AddUser(username);
                return Created(nameof(RegisterAsync), newUser);
            }
            finally
            {
                locker.Release();
            }
        }
    }
}
