using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RssApp.Config;

namespace RssApp.Filters;

/// <summary>
/// Rejects mutating HTTP requests (POST, PUT, DELETE, PATCH) when the app
/// is running in read-only replica mode. GET, HEAD, and OPTIONS are allowed.
/// </summary>
public class ReadOnlyActionFilter : IActionFilter
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS"
    };

    private readonly bool _isReadOnly;

    public ReadOnlyActionFilter(RssAppConfig config)
    {
        _isReadOnly = config.IsReadOnly;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (_isReadOnly && !AllowedMethods.Contains(context.HttpContext.Request.Method))
        {
            context.Result = new ObjectResult(new { error = "This replica is read-only." })
            {
                StatusCode = StatusCodes.Status405MethodNotAllowed
            };
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
