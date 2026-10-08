using System.Net;
using System.Net.Sockets;

namespace RssApp.RssClient;

/// <summary>
/// Thrown when an outbound request targets an address this server must never
/// contact (loopback, private, link-local, cloud metadata, ...). Derives from
/// HttpRequestException so existing fetch error handling treats it as a failed
/// fetch, while callers that want a clean 400 can catch it specifically.
/// </summary>
public sealed class BlockedOutboundUrlException : HttpRequestException
{
    public BlockedOutboundUrlException(string message) : base(message) { }
}

/// <summary>
/// Decides which destination addresses outbound HTTP may reach. Every feed URL
/// is user-supplied, so without this a subscriber could point the server at
/// 127.0.0.1, the Azure instance-metadata endpoint, or anything on the
/// container network, and read the response through the feed parser's error
/// messages. The policy is deny-by-range: anything not routable on the public
/// internet is blocked.
/// </summary>
public static class OutboundAddressPolicy
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip == null)
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedV4(ip.GetAddressBytes());
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsBlockedV6(ip);
        }

        // Anything that is not IPv4/IPv6 (e.g. a unix socket) is never a feed host.
        return true;
    }

    private static bool IsBlockedV4(byte[] b)
    {
        return b[0] switch
        {
            0 => true,                                   // 0.0.0.0/8 "this" network
            10 => true,                                  // 10.0.0.0/8 private
            100 when (b[1] & 0xC0) == 64 => true,        // 100.64.0.0/10 carrier NAT
            127 => true,                                 // 127.0.0.0/8 loopback
            169 when b[1] == 254 => true,                // 169.254.0.0/16 link-local, incl. 169.254.169.254 metadata
            172 when (b[1] & 0xF0) == 16 => true,        // 172.16.0.0/12 private
            192 when b[1] == 0 && b[2] == 0 => true,     // 192.0.0.0/24 IETF protocol assignments
            192 when b[1] == 0 && b[2] == 2 => true,     // 192.0.2.0/24 documentation
            192 when b[1] == 168 => true,                // 192.168.0.0/16 private
            198 when (b[1] & 0xFE) == 18 => true,        // 198.18.0.0/15 benchmarking
            198 when b[1] == 51 && b[2] == 100 => true,  // 198.51.100.0/24 documentation
            203 when b[1] == 0 && b[2] == 113 => true,   // 203.0.113.0/24 documentation
            >= 224 => true,                              // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast
            _ => false,
        };
    }

    private static bool IsBlockedV6(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
        {
            return true;
        }

        var b = ip.GetAddressBytes();

        // 64:ff9b::/96 NAT64 and 2002::/16 6to4 embed an IPv4 address; judge that address.
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            return IsBlockedV4([b[12], b[13], b[14], b[15]]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlockedV4([b[2], b[3], b[4], b[5]]);
        }

        // 2001:0::/32 Teredo (embeds an obfuscated IPv4 address) and 2001:db8::/32 documentation.
        if (b[0] == 0x20 && b[1] == 0x01 && ((b[2] == 0 && b[3] == 0) || (b[2] == 0x0d && b[3] == 0xb8)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Host names that never belong to a public feed, judged before any DNS
    /// lookup: "localhost", mDNS/.local, container-internal suffixes, and
    /// single-label names (which only resolve inside the hosting network).
    /// </summary>
    public static bool IsBlockedHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        host = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (host == "localhost" || !host.Contains('.'))
        {
            return true;
        }

        return host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".localdomain", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".home.arpa", StringComparison.Ordinal);
    }
}

/// <summary>
/// Validates a destination URL before it is fetched: scheme, host name, and
/// every address the host resolves to. Used by the message handler on every
/// hop (so redirects are re-checked) and by the feed controller up front (so a
/// blocked URL is a 400 rather than a stored feed that fails forever).
/// </summary>
public sealed class OutboundUrlGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;

    public OutboundUrlGuard() : this(Dns.GetHostAddressesAsync) { }

    /// <summary>Resolver is injectable so tests can run offline.</summary>
    public OutboundUrlGuard(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    {
        this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    public async Task ValidateAsync(Uri uri, CancellationToken token = default)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            throw new BlockedOutboundUrlException("Feed URL must be absolute.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new BlockedOutboundUrlException($"Feed URL scheme '{uri.Scheme}' is not allowed; only http and https are.");
        }

        var host = uri.IdnHost;

        // Numeric hosts first. IPAddress.TryParse also accepts the odd spellings
        // (decimal "2130706433", octal/hex octets) that a resolver would quietly
        // turn into 127.0.0.1, so they are judged as addresses, not names.
        if (IPAddress.TryParse(host, out var literal))
        {
            if (OutboundAddressPolicy.IsBlocked(literal))
            {
                throw new BlockedOutboundUrlException($"Feed host {host} is not a public address.");
            }

            return;
        }

        if (OutboundAddressPolicy.IsBlockedHostName(host))
        {
            throw new BlockedOutboundUrlException($"Feed host '{host}' is not a public host name.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await this.resolve(host, token);
        }
        catch (SocketException)
        {
            // Unresolvable hosts are an ordinary fetch failure, not a policy
            // violation; let the real request produce the usual error.
            return;
        }

        // Strict: one blocked record poisons the whole name, so a host that
        // mixes public and private answers cannot be used to reach the private one.
        if (addresses.Length > 0 && addresses.Any(OutboundAddressPolicy.IsBlocked))
        {
            throw new BlockedOutboundUrlException($"Feed host '{host}' resolves to a non-public address.");
        }
    }
}

/// <summary>
/// Message handler that validates every request URI, including each redirect
/// hop produced by RedirectDowngradeHandler when registered after it.
/// </summary>
public sealed class OutboundUrlGuardHandler : DelegatingHandler
{
    private readonly OutboundUrlGuard guard;

    public OutboundUrlGuardHandler(OutboundUrlGuard guard)
    {
        this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await this.guard.ValidateAsync(request.RequestUri, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// SocketsHttpHandler.ConnectCallback that resolves the host itself and only
/// connects to addresses the policy allows. This closes the gap the handler
/// above leaves: a DNS answer can change between the pre-check and the
/// connect (DNS rebinding), and this is the point where the socket actually
/// opens, so it is the check that cannot be raced.
/// </summary>
public static class GuardedConnect
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var endpoint = context.DnsEndPoint;
        IPAddress[] addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, token);

        if (addresses.Length == 0 || addresses.Any(OutboundAddressPolicy.IsBlocked))
        {
            throw new BlockedOutboundUrlException($"Host '{endpoint.Host}' resolves to a non-public address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
