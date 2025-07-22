using System.Xml;
using RssApp.Contracts;

namespace RssApp.Serialization;

public static class OpmlSerializer
{
    public static string GenerateOpmlContent(IEnumerable<NewsFeed> feeds)
    {
        XmlDocument doc = new XmlDocument();
        
        // Create the OPML structure
        XmlDeclaration xmlDeclaration = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
        doc.AppendChild(xmlDeclaration);
        
        XmlElement opmlElement = doc.CreateElement("opml");
        opmlElement.SetAttribute("version", "2.0");
        doc.AppendChild(opmlElement);
        
        // Add head element
        XmlElement headElement = doc.CreateElement("head");
        opmlElement.AppendChild(headElement);
        
        XmlElement titleElement = doc.CreateElement("title");
        titleElement.InnerText = "RSS Feed Export";
        headElement.AppendChild(titleElement);
        
        XmlElement dateCreatedElement = doc.CreateElement("dateCreated");
        dateCreatedElement.InnerText = DateTime.UtcNow.ToString("r");
        headElement.AppendChild(dateCreatedElement);
        
        // Add body element with all feeds
        XmlElement bodyElement = doc.CreateElement("body");
        opmlElement.AppendChild(bodyElement);
        
        foreach (var feed in feeds)
        {
            XmlElement outlineElement = doc.CreateElement("outline");
            outlineElement.SetAttribute("type", "rss");
            outlineElement.SetAttribute("xmlUrl", feed.Href);
            
            // Add title if available, otherwise use URL
            outlineElement.SetAttribute("text", feed.Href);
            
            // Add tags as category attribute if available
            if (feed.Tags != null && feed.Tags.Any())
            {
                outlineElement.SetAttribute("category", string.Join(",", feed.Tags));
            }
            
            bodyElement.AppendChild(outlineElement);
        }
        
        using var stringWriter = new StringWriter();
        using var xmlWriter = new XmlTextWriter(stringWriter) { Formatting = Formatting.Indented };
        doc.WriteContentTo(xmlWriter);
        
        return stringWriter.ToString();
    }

    public static IEnumerable<NewsFeed> ParseOpmlContent(string opmlContent, int userId)
    {
        var feeds = new List<NewsFeed>();
        
        try
        {
            // Enforce a reasonable size limit
            if (opmlContent.Length > 1_000_000) // 1MB limit
            {
                throw new ArgumentException("OPML content exceeds maximum allowed size");
            }

            XmlDocument doc = new XmlDocument();
            // Disable potentially dangerous features
            doc.XmlResolver = null;
            
            // Set secure parsing settings
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1_000_000,
                XmlResolver = null,
                ValidationType = ValidationType.None
            };
            
            using (var stringReader = new StringReader(opmlContent))
            using (var reader = XmlReader.Create(stringReader, settings))
            {
                doc.Load(reader);
                
                // Find all outline elements with type="rss"
                XmlNodeList? outlineNodes = doc.SelectNodes("//outline[@type='rss']");
                
                if (outlineNodes != null)
                {
                    foreach (XmlNode node in outlineNodes)
                    {
                        string? xmlUrl = node.Attributes?["xmlUrl"]?.Value;
                        
                        if (!string.IsNullOrEmpty(xmlUrl))
                        {
                            var feed = new NewsFeed(xmlUrl, userId);
                            
                            // Get title if available
                            //feed.Title = node.Attributes?["text"]?.Value ?? node.Attributes?["title"]?.Value;
                            
                            // Get categories/tags if available
                            string? categories = node.Attributes?["category"]?.Value;
                            if (!string.IsNullOrEmpty(categories))
                            {
                                foreach (var tag in categories.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    feed.Tags.Add(tag.Trim());
                                }
                            }
                            
                            feeds.Add(feed);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log exception or handle appropriately
            throw new FormatException("Error parsing OPML content", ex);
        }
        
        return feeds;
    }
}