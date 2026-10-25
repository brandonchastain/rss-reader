#nullable enable
using System.Security.Claims;
using RssApp.Contracts;
using RssApp.Data;
using Server.Authentication;

namespace RssApp.ComponentServices;

/// <summary>
/// Resolves the authenticated RssUser from claims. The NameIdentifier claim is
/// a login identity (provider:subject from the API login, or a legacy Static
/// Web Apps id) and is looked up in UserIdentities.
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
    /// New users are created with the login id as their username (email never stored).
    /// </summary>
    RssUser ResolveOrCreateUser(ClaimsPrincipal principal);
}

public class UserResolver : IUserResolver
{
    private readonly IUserRepository _userRepository;
    private readonly ILogger<UserResolver> _logger;

    private readonly IFeedRepository? _feedRepository;

    public UserResolver(IUserRepository userRepository, ILogger<UserResolver> logger, IFeedRepository? feedRepository = null)
    {
        _userRepository = userRepository;
        _logger = logger;
        _feedRepository = feedRepository;
    }

    public RssUser? ResolveUser(ClaimsPrincipal principal)
    {
        var loginId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(loginId))
            return null;

        var (provider, subject) = UserIdentity.ParseIdentity(loginId);
        var user = _userRepository.FindByIdentity(provider, subject);
        if (user != null)
        {
            // Lazy migration: replace email-based usernames with the login id
            if (user.Username != loginId && UserIdentity.LooksLikeEmail(user.Username))
            {
                _logger.LogInformation("Migrating username for user {UserId} from legacy value to login id", user.Id);
                _userRepository.UpdateUsername(user.Id, loginId);
                user.Username = loginId;
            }
            LinkFromClaim(principal, user, UserIdentity.LinkedUserIdClaim);
            return user;
        }

        // First direct login of an account linked by email hash during the
        // Static Web Apps migration window: find the row by its email-link
        // identity, then attach the real provider identity to it.
        var emailLink = principal.FindFirst(UserIdentity.EmailLinkClaim)?.Value;
        if (!string.IsNullOrEmpty(emailLink))
        {
            var (linkProvider, linkSubject) = UserIdentity.ParseIdentity(emailLink);
            user = _userRepository.FindByIdentity(linkProvider, linkSubject);
            if (user != null)
            {
                TryLink(user, provider, subject, "Linked {Provider} identity to user {UserId} through the email link");
                return user;
            }
        }

        return null;
    }

    // During the migration the SWA principal carries a link id (the Entra
    // object id when available, otherwise a hash of the Microsoft-reported
    // email). Attach it as an additional identity so the row survives cutover.
    private void LinkFromClaim(ClaimsPrincipal principal, RssUser user, string claimType)
    {
        var linked = principal.FindFirst(claimType)?.Value;
        if (string.IsNullOrEmpty(linked) || user.Identities.Contains(linked, StringComparer.Ordinal))
            return;

        var (provider, subject) = UserIdentity.ParseIdentity(linked);
        TryLink(user, provider, subject, "Linked user {UserId} to {Provider} identity for the login migration");
    }

    private void TryLink(RssUser user, string provider, string subject, string message)
    {
        try
        {
            _userRepository.LinkIdentity(user.Id, provider, subject);
            user.Identities = user.Identities.Append(UserIdentity.FormatIdentity(provider, subject)).ToList();
            _logger.LogInformation(message, provider, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to link identity on user {UserId}; will retry on next request", user.Id);
        }
    }

    public RssUser ResolveOrCreateUser(ClaimsPrincipal principal)
    {
        var existing = ResolveUser(principal);
        if (existing != null)
            return existing;

        var loginId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(loginId))
            throw new InvalidOperationException("Cannot create user without a login identifier");

        // New user: the login id is the username (email is never stored)
        var newUser = _userRepository.AddUser(loginId);
        var (provider, subject) = UserIdentity.ParseIdentity(loginId);
        try
        {
            _userRepository.LinkIdentity(newUser.Id, provider, subject);
            newUser.Identities = new[] { UserIdentity.FormatIdentity(provider, subject) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to link identity on newly created user {UserId}; will retry on next login", newUser.Id);
        }
        SubscribeToStarterFeeds(newUser);
        _logger.LogInformation("Created anonymous user {UserId} with login id", newUser.Id);
        return newUser;
    }

    // A new account starts with a small introductory set so the first timeline is
    // not empty. Failing here must never block sign-in.
    private void SubscribeToStarterFeeds(RssUser user)
    {
        if (_feedRepository == null) return;
        try
        {
            _feedRepository.AddFeeds(user, StarterFeeds.Urls.Select(url => new NewsFeed(url, user.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add starter feeds for new user {UserId}", user.Id);
        }
    }
}
