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

    public IEnumerable<NewsFeedItem> FromString(string responseContent)
    {
        try
        {
            var xmlDoc = XDocument.Parse(responseContent);
            if (responseContent.Contains("<rss"))
            {
                XmlSerializer xs = new XmlSerializer(typeof(RssDocument));
                var reader = new StringReader(responseContent);
                RssDocument rssFeedModel = (RssDocument)xs.Deserialize(reader);
                return rssFeedModel.Feed.Entries.Select(x => new NewsFeedItem(x.Id, x.Title, x.Link.Href, x.CommentsLink?.Href, x.PublishDate, x.Description));
            }
            else if (responseContent.Contains("<feed"))
            {
                XmlSerializer xs = new XmlSerializer(typeof(AtomFeed));
                var reader = new StringReader(responseContent);
                AtomFeed rssFeedModel = (AtomFeed)xs.Deserialize(reader);
                return rssFeedModel.Entries.Select(x => new NewsFeedItem(x.Id, x.Title, x.AltLink.Href, null, x.PublishDate, x.Content));
            }
            else
            {
                throw new InvalidDataException("invalid rss feed");
            }
        }
        catch (Exception ex)
        {
            this.logger.LogInformation(responseContent);
            throw;
        }
    }
}