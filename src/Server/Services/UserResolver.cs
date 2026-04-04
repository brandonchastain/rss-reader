#nullable enable
using System.Security.Claims;
using RssApp.Contracts;
using RssApp.Data;

namespace RssApp.ComponentServices;

/// <summary>
/// Resolves the authenticated RssUser from claims, using AAD GUID as the
/// primary identifier with email fallback for existing users. Handles
/// lazy migration of existing users to AAD GUID-based lookup.
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
    /// New users are created with the AAD GUID as their username (email never stored).
    /// Existing users found by email are migrated to AAD GUID-based lookup.
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
        var email = principal.FindFirst(ClaimTypes.Name)?.Value;

        // Primary lookup: AAD GUID
        if (!string.IsNullOrEmpty(aadId))
        {
            var user = _userRepository.GetUserByAadId(aadId);
            if (user != null)
                return user;
        }

        // Fallback: email-based lookup for existing users not yet migrated
        if (!string.IsNullOrEmpty(email))
        {
            var user = _userRepository.GetUserByName(email);
            if (user != null)
            {
                // Lazy migration: set the AadUserId so future lookups use the GUID.
                // Wrapped in try-catch so a DB error (e.g. unique constraint race)
                // doesn't break the login — the user is still valid.
                if (!string.IsNullOrEmpty(aadId))
                {
                    try
                    {
                        _userRepository.SetAadUserId(user.Id, aadId);
                        _logger.LogInformation("Migrated user {UserId} to AAD GUID-based lookup", user.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to lazy-migrate AadUserId for user {UserId}; will retry on next login", user.Id);
                    }
                }
                return user;
            }
        }

        return null;
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
