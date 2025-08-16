using System.Globalization;
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

    public IEnumerable<NewsFeedItem> FromString(string responseContent, RssUser user)
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
                return rdfFeedModel.Items.Select(x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided
                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.Link.Href,
                        x.CommentsLink?.Href,
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
                return rssFeedModel.Feed.Entries.Select(x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided

                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.Link.Href,
                        x.CommentsLink?.Href,
                        date,
                        x.Description,
                        x.MediaContents?.FirstOrDefault()?.Url);

                    item.PublishDateOrder = item.ParsedDate?.Ticks ?? DateTime.UtcNow.Ticks;
                    return item;
                });
            }
            else if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(AtomFeed));
                var reader = new StringReader(responseContent);
                AtomFeed rssFeedModel = (AtomFeed)xs.Deserialize(reader);
                return rssFeedModel.Entries.Select(x =>
                {
                    var date = FormatDateString(x.PublishDate) ?? FormatDateString(defaultDate.ToString()); // Default to 1 day ago if no date is provided
                    var item = new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.AltLink?.Href ?? x.Links.FirstOrDefault()?.Href,
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

    public static string FormatDateString(string dateString)
    {
        var date = ParseDateTime(dateString);
        if (date.HasValue)
        {
            return date.Value.ToString(IsoDateFormat, CultureInfo.InvariantCulture);
        }

        return null;
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
