using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;
using RssApp.Data;
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

    public AdminController(
        ISystemStatsRepository statsRepo,
        IUserRepository userRepo,
        RssAppConfig config,
        IUserResolver userResolver)
    {
        _statsRepo    = statsRepo    ?? throw new ArgumentNullException(nameof(statsRepo));
        _userRepo     = userRepo     ?? throw new ArgumentNullException(nameof(userRepo));
        _config       = config       ?? throw new ArgumentNullException(nameof(config));
        _userResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
    }

    // GET /api/admin/stats
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        if (!IsAdmin(User))
            return Forbid();

        var snapshot = _statsRepo.GetLatestSnapshot();
        if (snapshot == null)
            snapshot = new SystemStatsSnapshot { Timestamp = DateTime.UtcNow };

        return Ok(snapshot);
    }

    // GET /api/admin/stats/history
    [HttpGet("stats/history")]
    public IActionResult GetStatsHistory()
    {
        if (!IsAdmin(User))
            return Forbid();

        var history = _statsRepo.GetHistory(30).ToList();
        return Ok(history);
    }

    // GET /api/admin/users
    [HttpGet("users")]
    public IActionResult GetUsers()
    {
        if (!IsAdmin(User))
            return Forbid();

        var users = _userRepo.GetAllUsers()
            .Select(u => new { u.Id, u.Username })
            .ToList();

        return Ok(users);
    }

    private bool IsAdmin(ClaimsPrincipal principal)
    {
        if (_config.IsTestUserEnabled)
            return true;

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
            return false;

        var adminIds = _config.AdminAadUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return adminIds.Contains(userId, StringComparer.OrdinalIgnoreCase);
    }
}
