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

    // GET /api/admin/stats/history
    [HttpGet("stats/history")]
    public IActionResult GetStatsHistory()
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        var history = _statsRepo.GetHistory(30).ToList();
        return Ok(history);
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

    public sealed class LinkRequest
    {
        public string ProviderUserId { get; set; } = string.Empty;
    }

    // POST /api/admin/users/{id}/link  { "providerUserId": "microsoft:<oid>" }
    //
    // Migration safety net. Rows created under Static Web Apps carry an opaque
    // SWA id; the API login identifies the same person as provider:subject. The
    // SWA handler links the two automatically when the principal carries the
    // Entra object id. When it does not, an admin writes the link here, through
    // the repository (so the in-memory user cache is evicted), before the proxy
    // goes away.
    [HttpPost("users/{id:int}/link")]
    public IActionResult LinkProviderUserId(int id, [FromBody] LinkRequest request)
    {
        if (!Server.Authentication.AdminPolicy.IsAdmin(_config, User, _userResolver.ResolveUser(User)))
            return Forbid();

        var providerUserId = request?.ProviderUserId?.Trim() ?? string.Empty;
        var colon = providerUserId.IndexOf(':');
        if (colon <= 0 || colon == providerUserId.Length - 1
            || !Server.Authentication.UserIdentity.IsKnownProvider(providerUserId[..colon]))
        {
            return BadRequest(new { error = "providerUserId must be <google|microsoft|github>:<subject>" });
        }
        providerUserId = Server.Authentication.UserIdentity.BuildUserId(providerUserId[..colon], providerUserId[(colon + 1)..]);

        var user = _userRepo.GetUserById(id);
        if (user == null)
            return NotFound();

        var holder = _userRepo.GetUserByProviderUserId(providerUserId) ?? _userRepo.GetUserByAadId(providerUserId);
        if (holder != null && holder.Id != id)
            return Conflict(new { error = $"User {holder.Id} already holds {providerUserId}" });

        _userRepo.SetProviderUserId(id, providerUserId);
        return Ok(new { user.Id, user.Username, user.AadUserId, ProviderUserId = providerUserId });
    }
}
