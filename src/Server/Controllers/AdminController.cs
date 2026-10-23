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

    public AdminController(
        ISystemStatsRepository statsRepo,
        IUserRepository userRepo,
        RssAppConfig config,
        IUserResolver userResolver,
        SystemStatsCollector statsCollector)
    {
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

    // GET /api/admin/stats/history?days=30   (1, 7, 30 or 365)
    [HttpGet("stats/history")]
    public IActionResult GetStatsHistory([FromQuery] int days = 30)
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        if (!RssReader.Server.Services.StatsHistoryRange.IsAllowed(days))
            return BadRequest($"days must be one of {string.Join(", ", RssReader.Server.Services.StatsHistoryRange.AllowedDays)}.");

        var history = _statsRepo.GetHistory(days).ToList();
        return Ok(RssReader.Server.Services.StatsHistoryRange.Thin(history));
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
