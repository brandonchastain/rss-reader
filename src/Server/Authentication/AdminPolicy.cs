#nullable enable
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using RssApp.ComponentServices;
using RssApp.Config;
using RssApp.Contracts;

namespace Server.Authentication;

/// <summary>Requirement behind the "Admin" authorization policy.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Evaluates <see cref="AdminPolicy.IsAdmin"/> for [Authorize(Policy = AdminPolicy.Name)].
/// A signed-in caller who is not an admin fails the policy and gets 403.
/// </summary>
public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    private readonly RssAppConfig config;
    private readonly IUserResolver userResolver;

    public AdminAuthorizationHandler(RssAppConfig config, IUserResolver userResolver)
    {
        this.config = config;
        this.userResolver = userResolver;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && AdminPolicy.IsAdmin(this.config, context.User, this.userResolver.ResolveUser(context.User)))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Admin membership is a list of user ids in config. A row may carry several
/// login identities (its Static Web Apps id, its provider ids), so any stored
/// identity, as a bare subject or as provider:subject, matches the list.
/// </summary>
public static class AdminPolicy
{
    /// <summary>Authorization policy name: [Authorize(Policy = AdminPolicy.Name)].</summary>
    public const string Name = "Admin";

    public static bool IsAdmin(RssAppConfig config, ClaimsPrincipal principal, RssUser? user = null)
    {
        if (config.IsTestUserEnabled)
            return true;

        var adminIds = config.AdminAadUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (adminIds.Length == 0)
            return false;

        var candidates = new List<string?>
        {
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            principal.FindFirst(UserIdentity.LinkedUserIdClaim)?.Value,
            user?.AadUserId,
            user?.ProviderUserId,
        };

        if (user != null)
        {
            foreach (var identity in user.Identities)
            {
                candidates.Add(identity);
                candidates.Add(UserIdentity.ParseIdentity(identity).Subject);
            }
        }

        return candidates.Any(c => !string.IsNullOrEmpty(c) && adminIds.Contains(c, StringComparer.OrdinalIgnoreCase));
    }
}
