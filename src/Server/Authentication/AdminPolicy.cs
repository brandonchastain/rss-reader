#nullable enable
using System.Security.Claims;
using RssApp.Config;
using RssApp.Contracts;

namespace Server.Authentication;

/// <summary>
/// Admin membership is a list of user ids in config. A row may carry several
/// login identities (its Static Web Apps id, its provider ids), so any stored
/// identity, as a bare subject or as provider:subject, matches the list.
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
