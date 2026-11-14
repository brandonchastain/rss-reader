#nullable enable
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RssApp.ComponentServices;
using RssApp.Contracts;

namespace Server.Authentication;

/// <summary>
/// Base for controllers whose actions act on the signed-in user's own data.
/// The caller is resolved once per request from the session principal (never
/// from anything the request body or query says) and exposed as CurrentUser.
/// <see cref="RequireRssUserAttribute"/> answers 404 before the action runs
/// when the principal has no RssUser row yet, so an action never sees null.
///
/// This settles who the caller is. Whether the caller may touch a given post
/// or feed is still the repository's job: every lookup by id or URL takes the
/// RssUser and scopes its SQL to that user.
/// </summary>
[Authorize]
[RequireRssUser]
public abstract class UserScopedController : ControllerBase
{
    private RssUser? currentUser;
    private bool resolved;

    protected UserScopedController(IUserResolver userResolver)
    {
        this.UserResolver = userResolver ?? throw new ArgumentNullException(nameof(userResolver));
    }

    protected IUserResolver UserResolver { get; }

    /// <summary>
    /// The signed-in caller's row. Non-null inside any action that is not marked
    /// <see cref="AllowUnregisteredUserAttribute"/> or AllowAnonymous.
    /// </summary>
    protected internal RssUser CurrentUser => this.TryGetCurrentUser()!;

    internal RssUser? TryGetCurrentUser()
    {
        if (!this.resolved)
        {
            this.currentUser = this.UserResolver.ResolveUser(this.User);
            this.resolved = true;
        }
        return this.currentUser;
    }

    /// <summary>
    /// Several request shapes still carry a UserId (the client fills in its own).
    /// The server never acts on it; a mismatch is refused so a client bug shows
    /// up as a 403 rather than a write to the caller's account it did not intend.
    /// </summary>
    protected bool IsCaller(int userId) => userId == this.CurrentUser.Id;
}

/// <summary>
/// Short-circuits with 404 "Authenticated user not found." when the principal
/// is signed in but has no RssUser row. The client treats that 404 as "call
/// register", so the status and body are part of the API contract.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class RequireRssUserAttribute : ActionFilterAttribute
{
    public const string NotFoundMessage = "Authenticated user not found.";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any() || metadata.OfType<AllowUnregisteredUserAttribute>().Any())
            return;

        if (context.Controller is UserScopedController controller && controller.TryGetCurrentUser() == null)
            context.Result = new NotFoundObjectResult(NotFoundMessage);
    }
}

/// <summary>Opts an action out of <see cref="RequireRssUserAttribute"/> (registration creates the row).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllowUnregisteredUserAttribute : Attribute
{
}
