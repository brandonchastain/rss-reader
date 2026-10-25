using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
using RssReader.Server.Services;
using System.Security.Claims;

namespace Server.Controllers;

[Authorize]
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly ISystemStatsRepository _statsRepo;
    private readonly IUserRepository _userRepo;
    private readonly RssAppConfig _config;
    private readonly IUserResolver _userResolver;
    private readonly SystemStatsCollector _statsCollector;
    private readonly AdminDetailQueries _details;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        ISystemStatsRepository statsRepo,
        IUserRepository userRepo,
        RssAppConfig config,
        IUserResolver userResolver,
        SystemStatsCollector statsCollector,
        AdminDetailQueries details,
        ILogger<AdminController> logger = null)
    {
        _logger         = logger;
        _details        = details        ?? throw new ArgumentNullException(nameof(details));
        _statsRepo      = statsRepo      ?? throw new ArgumentNullException(nameof(statsRepo));
        _userRepo       = userRepo       ?? throw new ArgumentNullException(nameof(userRepo));
        _config         = config         ?? throw new ArgumentNullException(nameof(config));
        _userResolver   = userResolver   ?? throw new ArgumentNullException(nameof(userResolver));
        _statsCollector = statsCollector ?? throw new ArgumentNullException(nameof(statsCollector));
    }

    // GET /api/admin/stats
    // Captures a fresh snapshot (at most hourly) only when the dashboard is opened;
    // nothing runs these COUNT(*) queries in the background any more.
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        var snapshot = await _statsCollector.GetOrCaptureAsync();
        return Ok(snapshot);
    }

    // POST /api/admin/stats/refresh: take a snapshot now instead of waiting
    // for the hourly cadence; returns it.
    [HttpPost("stats/refresh")]
    public async Task<IActionResult> RefreshStats()
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        var snapshot = await _statsCollector.CaptureNowAsync();
        return Ok(snapshot);
    }

    // GET /api/admin/stats/history?range=30d   (1h, 1d, 7d, 30d, 1y; ?days=N still accepted)
    [HttpGet("stats/history")]
    public IActionResult GetStatsHistory([FromQuery] string range = null, [FromQuery] int? days = null)
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        if (range == null && days == null) range = RssReader.Server.Services.StatsHistoryRange.DefaultRange;
        if (!RssReader.Server.Services.StatsHistoryRange.TryResolve(range, days, out var window))
            return BadRequest($"range must be one of {string.Join(", ", RssReader.Server.Services.StatsHistoryRange.Ranges.Keys)}.");

        var history = _statsRepo.GetHistory(window).ToList();
        return Ok(RssReader.Server.Services.StatsHistoryRange.Thin(history));
    }

    // GET /api/admin/detail/{users|feeds|posts|db}: the table behind a stat card.
    [HttpGet("detail/{section}")]
    public IActionResult GetDetail(string section)
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        var table = _details.Get(section);
        return table == null ? NotFound() : Ok(table);
    }

    // DELETE /api/admin/detail/{users|feeds|posts}/{id}
    // An admin cannot delete themself, so the dashboard can never be locked out.
    [HttpDelete("detail/{section}/{id:long}")]
    public IActionResult DeleteDetail(string section, long id)
    {
        var caller = _userResolver.ResolveUser(User);
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, caller))
            return Forbid();

        if (string.Equals(section, "users", StringComparison.OrdinalIgnoreCase) && caller != null && caller.Id == id)
            return BadRequest("You cannot delete your own admin user.");

        if (string.Equals(section, "users", StringComparison.OrdinalIgnoreCase))
        {
            // Through the user repository so its cache drops the user too.
            if (!_userRepo.GetAllUsers().Any(u => u.Id == id)) return NotFound();
            _userRepo.DeleteUser((int)id);
        }
        else if (!_details.Delete(section, id))
        {
            return NotFound();
        }

        _logger?.LogWarning("Admin deleted {Section} {Id}", section, id);
        return NoContent();
    }

    // GET /api/admin/users
    [HttpGet("users")]
    public IActionResult GetUsers()
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        // Build per-user stats lookup once (one indexed query) and join in-memory.
        var statsByUser = _statsRepo.GetPerUserStats()
            .ToDictionary(s => s.UserId);

        var users = _userRepo.GetAllUsers()
            .Select(u =>
            {
                statsByUser.TryGetValue(u.Id, out var stats);
                return new
                {
                    u.Id,
                    u.Username,
                    FeedCount = stats?.FeedCount ?? 0,
                    PostCount = stats?.ItemCount ?? 0
                };
            })
            .ToList();

        return Ok(users);
    }
}
