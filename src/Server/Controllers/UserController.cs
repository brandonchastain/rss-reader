using Microsoft.AspNetCore.Mvc;
using RssApp.Data;
using RssApp.Contracts;


namespace Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository userRepository;
        private readonly ILogger<UserController> logger;

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

        // POST: api/user
        [HttpPost]
        public IActionResult CreateUser([FromBody] RssUser user)
        {
            // Placeholder: create a new user
            return CreatedAtAction(nameof(RssUser), new { id = 1 }, user);
        }
    }
}
