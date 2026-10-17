#nullable enable
using System.Security.Claims;
using RssApp.Contracts;
using RssApp.Data;
using Server.Authentication;

namespace RssApp.ComponentServices;

/// <summary>
/// Resolves the authenticated RssUser from claims, using the SWA-provided
/// user identifier as the sole lookup key.
/// </summary>
public interface IUserResolver
{
    /// <summary>
    /// Resolves the authenticated user from the claims principal.
    /// Returns null if the user is not found and not being registered.
    /// </summary>
    RssUser? ResolveUser(ClaimsPrincipal principal);

    /// <summary>
    /// Resolves or creates the authenticated user during registration.
    /// New users are created with the SWA user ID as their username (email never stored).
    /// </summary>
    RssUser ResolveOrCreateUser(ClaimsPrincipal principal);
}

public class UserResolver : IUserResolver
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UserResolver> _logger;

    public UserResolver(IUserRepository userRepository, ILogger<UserResolver> logger)
    {
        _userRepository = userRepository;
        _logger = logger;
    }

    public RssUser? ResolveUser(ClaimsPrincipal principal)
    {
        var aadId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(aadId))
            return null;

        var user = _userRepository.GetUserByAadId(aadId);
        if (user != null)
        {
            // Lazy migration: replace email-based usernames with AAD GUID
            if (user.Username != aadId)
            {
                _logger.LogInformation("Migrating username for user {UserId} from legacy value to AAD GUID", user.Id);
                _userRepository.UpdateUsername(user.Id, aadId);
                user.Username = aadId;
            }
            LinkProviderUserId(principal, user);
            return user;
        }

        // Not found by the id on the principal: the person may have signed up
        // through Static Web Apps and now logs in through the API, in which
        // case the row carries their provider:subject id in ProviderUserId.
        return _userRepository.GetUserByProviderUserId(aadId);
    }

    // During the migration the SWA principal carries the provider-stable id
    // the API login will produce. Record it once so the row survives cutover.
    private void LinkProviderUserId(ClaimsPrincipal principal, RssUser user)
    {
        var linked = principal.FindFirst(UserIdentity.LinkedUserIdClaim)?.Value;
        if (string.IsNullOrEmpty(linked) || user.ProviderUserId == linked)
            return;

        try
        {
            _userRepository.SetProviderUserId(user.Id, linked);
            user.ProviderUserId = linked;
            _logger.LogInformation("Linked user {UserId} to provider id for the login migration", user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to link provider id on user {UserId}; will retry on next request", user.Id);
        }
    }

    public RssUser ResolveOrCreateUser(ClaimsPrincipal principal)
    {
        var existing = ResolveUser(principal);
        if (existing != null)
            return existing;

        var aadId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(aadId))
            throw new InvalidOperationException("Cannot create user without AAD identifier");

        // New user: use AAD GUID as username (email is never stored)
        var newUser = _userRepository.AddUser(aadId);
        try
        {
            _userRepository.SetAadUserId(newUser.Id, aadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set AadUserId on newly created user {UserId}; will retry on next login", newUser.Id);
        }
        _logger.LogInformation("Created anonymous user {UserId} with AAD GUID", newUser.Id);
        return newUser;
    }
}
