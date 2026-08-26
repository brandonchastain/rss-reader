using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;
using RssApp.Contracts;
using RssApp.Contracts.FeedTypes;
using Server.Controllers;

namespace RssApp.Serialization;

public class RssDeserializer
{
    private const string IsoDateFormat = "yyyy-MM-ddTHH:mm:ssK";
    private readonly ILogger<RssDeserializer> logger;
    public RssDeserializer(ILogger<RssDeserializer> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Parses a feed document into items. <paramref name="feedUrl"/> is the URL the
    /// document was fetched from; it is the last-resort base for resolving
    /// site-relative item links when the feed declares no base of its own.
    /// </summary>
    public IEnumerable<NewsFeedItem> FromString(string responseContent, RssUser user, string feedUrl = null)
    {
        var now = FormatDateString(DateTime.UtcNow.ToString(IsoDateFormat));
        var defaultDate = DateTime.UtcNow - TimeSpan.FromDays(7); // Default to 7 days ago if no date is provided
        try
        {
            // Strip out darkreader-related content
            responseContent = Regex.Replace(responseContent, "<[^>]*darkreader[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            responseContent = Regex.Replace(responseContent, "<[^>]*dark-theme[^>]*>", string.Empty, RegexOptions.IgnoreCase);
            responseContent = Regex.Replace(responseContent, "<[^>]*darker-dark-theme[^>]*>", string.Empty, RegexOptions.IgnoreCase);

            var xmlDoc = XDocument.Parse(responseContent);
            var root = xmlDoc.Root;
            if (root.Name.LocalName.Equals("rdf", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(RdfFeed));
                var reader = new StringReader(responseContent);
                RdfFeed rdfFeedModel = (RdfFeed)xs.Deserialize(reader);
                var rdfBase = ResolveBase(rdfFeedModel.Channel?.Link, feedUrl);
                return MapItems(rdfFeedModel.Items, x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided
                    var href = ResolveHref(x.Link?.Href ?? x.Id, rdfBase);
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        return null;
                    }

                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        CleanTitle(x.Title),
                        href,
                        ResolveHref(x.CommentsLink?.Href, rdfBase),
                        date,
                        x.Description,
                        thumbnailUrl: null);
                    item.PublishDateOrder = item.ParsedDate?.Ticks ?? DateTime.UtcNow.Ticks;
                    return item;
                });
            }
            else if (root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(RssDocument));
                var reader = new StringReader(responseContent);
                RssDocument rssFeedModel = (RssDocument)xs.Deserialize(reader);
                var rssChannel = rssFeedModel.Feed;
                var rssBase = ResolveBase(
                    rssChannel?.Link
                        ?? rssChannel?.AtomLinks?.FirstOrDefault(l => l.Rel == "alternate")?.Href
                        ?? rssChannel?.AtomLinks?.FirstOrDefault(l => l.Rel == "self")?.Href,
                    feedUrl);
                return MapItems(rssChannel?.Entries, x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided

                    // An item with no usable link can't be keyed or opened; fall back to
                    // its guid before giving up so a link-less item is still surfaced.
                    var href = ResolveHref(x.Link?.Href ?? x.Id, rssBase);
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        return null;
                    }

                    // Prefer an explicit media image; the content <img> scrape in
                    // ThumbnailResolver is the fallback for items without one.
                    var media = x.MediaContents?.FirstOrDefault()?.Url
                        ?? x.MediaThumbnails?.FirstOrDefault()?.Url
                        ?? (x.Enclosure?.Type?.StartsWith("image", StringComparison.OrdinalIgnoreCase) == true
                                ? x.Enclosure.Url
                                : null);

                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        CleanTitle(x.Title),
                        href,
                        ResolveHref(x.CommentsLink?.Href, rssBase),
                        date,
                        x.Description,
                        ResolveHref(media, rssBase));

                    item.PublishDateOrder = item.ParsedDate?.Ticks ?? DateTime.UtcNow.Ticks;
                    return item;
                });
            }
            else if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(AtomFeed));
                var reader = new StringReader(responseContent);
                AtomFeed rssFeedModel = (AtomFeed)xs.Deserialize(reader);
                var atomBase = ResolveBase(rssFeedModel.BaseHref, feedUrl);
                return MapItems(rssFeedModel.Entries, x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided
                    var href = ResolveHref(x.AltLink?.Href ?? x.Links?.FirstOrDefault()?.Href ?? x.Id, atomBase);
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        return null;
                    }

                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        CleanTitle(x.Title),
                        href,
                        commentsHref: null,
                        date,
                        x.Content?.ToString(),
                        thumbnailUrl: null);
                    item.PublishDateOrder = item.ParsedDate?.Ticks ?? DateTime.UtcNow.Ticks;
                    return item;
                });
            }
            else
            {
                throw new InvalidDataException("invalid document type");
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "rss entry deserialization exception");
            throw;
        }
    }

    /// <summary>
    /// Matches a URI scheme prefix per RFC 3986 (ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) ":"),
    /// which is what makes an href absolute.
    ///
    /// Uri.TryCreate(..., UriKind.Absolute, ...) cannot be used for this test: on Unix it
    /// parses a site-relative "/article/123/slug" as an absolute file:// URI, so relative
    /// links were passed through unresolved on Linux while resolving correctly on Windows.
    /// A protocol-relative "//host/path" has no scheme and is deliberately left to be
    /// resolved against the base, which supplies the right one.
    /// </summary>
    private static readonly Regex HasUriScheme = new Regex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.Compiled);

    /// <summary>
    /// Picks the base URL to resolve site-relative item links against: the base the
    /// feed declares (channel &lt;link&gt;, atom rel="alternate"/"self"), falling back
    /// to the URL the feed itself was fetched from. Returns null when neither is a
    /// usable absolute URL, in which case relative links are left untouched.
    /// </summary>
    private static Uri ResolveBase(string declaredBase, string feedUrl)
    {
        foreach (var candidate in new[] { declaredBase, feedUrl })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Makes an item link absolute. Feeds like retronauts.com publish links as
    /// "/article/123/slug"; stored that way they resolve against the reader's own
    /// origin and 404 when opened. Absolute links, and anything we cannot resolve,
    /// are returned unchanged.
    /// </summary>
    private static string ResolveHref(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return href;
        }

        var trimmed = href.Trim();
        if (HasUriScheme.IsMatch(trimmed))
        {
            return trimmed;
        }

        if (baseUri == null)
        {
            return trimmed;
        }

        return Uri.TryCreate(baseUri, trimmed, out var absolute) ? absolute.ToString() : trimmed;
    }

    /// <summary>
    /// Projects feed entries to <see cref="NewsFeedItem"/>s, isolating each entry so a
    /// single malformed item (e.g. one missing a link) is skipped instead of throwing
    /// out the entire feed. A mapper may also return null to deliberately skip an entry.
    /// Materialized eagerly so any per-item exception is contained here, not surfaced
    /// later when the caller enumerates the result.
    /// </summary>
    private List<NewsFeedItem> MapItems<TSource>(IEnumerable<TSource> entries, Func<TSource, NewsFeedItem> map)
    {
        var items = new List<NewsFeedItem>();
        if (entries == null)
        {
            return items;
        }

        foreach (var entry in entries)
        {
            try
            {
                var item = map(entry);
                if (item != null)
                {
                    items.Add(item);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Skipping malformed feed item during deserialization");
            }
        }

        return items;
    }

    public static string FormatDateString(string dateString)
    {
        var date = ParseDateTime(dateString);
        if (date.HasValue)
        {
            return date.Value.ToString(IsoDateFormat, CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>
    /// Strips HTML tags and decodes HTML entities from feed titles.
    /// RSS feeds sometimes embed markup (e.g. &lt;i&gt;) in title elements.
    /// </summary>
    internal static string CleanTitle(string title)
    {
        if (string.IsNullOrEmpty(title))
            return title;

        // Strip HTML tags
        var cleaned = Regex.Replace(title, "<[^>]+>", string.Empty);
        // Decode HTML entities (&amp; → &, &#39; → ', etc.)
        cleaned = WebUtility.HtmlDecode(cleaned);
        return cleaned.Trim();
    }

    private static DateTime? ParseDateTime(string dateString)
    {
        if (string.IsNullOrWhiteSpace(dateString))
        {
            return null;
        }

        dateString = dateString.Trim();

        // Replace timezone abbreviations with their standard format
        dateString = TimeZoneConverter.ConvertTimeZoneAbbreviation(dateString);

        // Try Unix timestamp (seconds since Unix epoch)
        if (long.TryParse(dateString, out long unixTimestamp))
        {
            try
            {
                // Check if this is a reasonable Unix timestamp (between 1970 and 2100)
                if (unixTimestamp > 0 && unixTimestamp < 4102444800) // 1/1/2100
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
                }
            }
            catch
            {
                // If conversion fails, continue with other formats
            }
        }

        // Common date formats
        string[] formats = {
            // RFC 1123 / RFC 2822
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss zzzz", // Four-digit timezone offset
            "ddd, dd MMM yyyy HH:mm:ss",
            "ddd, d MMM yyyy HH:mm:ss zzz",
            "ddd, d MMM yyyy HH:mm:ss zzzz", // Four-digit timezone offset
            "ddd, d MMM yyyy HH:mm:ss",
            
            // ISO 8601
            "yyyy-MM-ddTHH:mm:ssK",
            "yyyy-MM-ddTHH:mm:ss.fffffffK",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd",
            
            // Other common formats
            "MM/dd/yyyy HH:mm:ss",
            "MM/dd/yyyy",
            "dd/MM/yyyy HH:mm:ss",
            "dd/MM/yyyy",
            "yyyyMMddTHHmmssZ"
        };

        // Try parsing with explicit formats
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture, 
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, 
                                      out DateTime result))
            {
                return result;
            }
        }

        // Try with general DateTime parsing as a fallback
        var styles = DateTimeStyles.AdjustToUniversal 
                    | DateTimeStyles.AssumeUniversal 
                    | DateTimeStyles.AllowWhiteSpaces;

        if (DateTime.TryParse(dateString, CultureInfo.InvariantCulture, styles, out DateTime parsedDate))
        {
            return parsedDate;
        }

        // If all parsing attempts fail, return null
        return null;
    }
}
