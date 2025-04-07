using System.Xml.Linq;
using System.Xml.Serialization;
using RssApp.Contracts;

namespace RssApp.Serialization;

public class RssDeserializer
{
    private readonly ILogger<RssDeserializer> logger;
    public RssDeserializer(ILogger<RssDeserializer> logger)
    {
        this.logger = logger;
    }

    public IEnumerable<NewsFeedItem> FromString(string responseContent, RssUser user)
    {
        try
        {
            var xmlDoc = XDocument.Parse(responseContent);
            var root = xmlDoc.Root;
            if (root.Name.LocalName.Equals("rdf", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(RdfFeed));
                var reader = new StringReader(responseContent);
                RdfFeed rdfFeedModel = (RdfFeed)xs.Deserialize(reader);
                return rdfFeedModel.Items.Select(x => 
                {
                    return new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.Link.Href,
                        x.CommentsLink?.Href,
                        FormatDateString(x.PublishDate),
                        x.Description,
                        thumbnailUrl: null);
                });
            }
            else if (root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(RssDocument));
                var reader = new StringReader(responseContent);
                RssDocument rssFeedModel = (RssDocument)xs.Deserialize(reader);
                return rssFeedModel.Feed.Entries.Select(
                    x => new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.Link.Href,
                        x.CommentsLink?.Href,
                        FormatDateString(x.PublishDate),
                        x.Description,
                        x.MediaContents?.FirstOrDefault()?.Url));
            }
            else if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                XmlSerializer xs = new XmlSerializer(typeof(AtomFeed));
                var reader = new StringReader(responseContent);
                AtomFeed rssFeedModel = (AtomFeed)xs.Deserialize(reader);
                return rssFeedModel.Entries.Select(
                    x => new NewsFeedItem(
                        x.Id,
                        user.Id,
                        x.Title,
                        x.AltLink?.Href ?? x.Links.FirstOrDefault()?.Href,
                        commentsHref: null,
                        FormatDateString(x.PublishDate),
                        x.Content?.ToString(),
                        thumbnailUrl: null));
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

    private static string FormatDateString(string dateString)
    {
        if (string.IsNullOrEmpty(dateString))
        {
            return string.Empty;
        }

        if (DateTime.TryParse(dateString, out DateTime parsedDate))
        {
            // output ISO 8601 format
            return parsedDate.ToString("yyyy-MM-ddTHH:mm:ssK");
        }

        // If parsing fails, return the original string or handle it as needed
        return dateString;
    }
}