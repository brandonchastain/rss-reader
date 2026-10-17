#nullable enable
using System.Security.Claims;
using RssApp.Config;
using RssApp.Contracts;

namespace Server.Authentication;

/// <summary>
/// Admin membership is a list of user ids in config. An admin's row carries
/// both its Static Web Apps id and, once linked, its provider id, so either
/// value in AdminAadUserIds keeps working across the migration.
/// </summary>
public static class AdminPolicy
{
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

        return candidates.Any(c => !string.IsNullOrEmpty(c) && adminIds.Contains(c, StringComparer.OrdinalIgnoreCase));
    }
}
