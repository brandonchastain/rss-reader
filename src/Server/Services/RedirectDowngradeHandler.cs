using System.Net;

namespace RssApp.RssClient;

/// <summary>
/// A custom HTTP request handler that follows redirects and allows downgrades from HTTPS to HTTP.
/// This is useful for handling cases where a feed URL redirects from HTTPS to HTTP.
/// The standard HttpClientHandler (with AllowAutoRedirect enabled) does not support such a downgrade.
/// </summary>
public class RedirectDowngradeHandler : DelegatingHandler
{
    private const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var redirectCount = 0;
        var currentRequest = request;

        while (redirectCount < MaxRedirects)
        {
            var response = await base.SendAsync(currentRequest, cancellationToken);

            if (!IsRedirect(response))
            {
                return response;
            }

            redirectCount++;
            var location = response.Headers.Location;

            if (location == null)
            {
                return response; // No location header, can't redirect.
            }

            if (!location.IsAbsoluteUri)
            {
                location = new Uri(currentRequest.RequestUri, location);
            }

            var nextRequest = new HttpRequestMessage(HttpMethod.Get, location);

            // Copy headers from the original request.
            foreach (var header in request.Headers)
            {
                nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            currentRequest = nextRequest;
        }

        throw new HttpRequestException($"Too many redirects for {request.RequestUri}");
    }

    private static bool IsRedirect(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.MovedPermanently: // 301
            case HttpStatusCode.Found: // 302
            case HttpStatusCode.SeeOther: // 303
            case HttpStatusCode.TemporaryRedirect: // 307
            case (HttpStatusCode)308: // Permanent Redirect
                return response.Headers.Location != null;
            default:
                return false;
        }
    }
}
