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
        
        // Group feeds by tags to create nested structure (OPML standard approach)
        // Also track feeds without tags
        var tagToFeeds = new Dictionary<string, List<NewsFeed>>();
        var feedsWithoutTags = new List<NewsFeed>();
        
        foreach (var feed in feeds)
        {
            if (feed.Tags == null || !feed.Tags.Any())
            {
                feedsWithoutTags.Add(feed);
            }
            else
            {
                // Add feed to each tag it belongs to
                foreach (var tag in feed.Tags)
                {
                    if (!tagToFeeds.ContainsKey(tag))
                    {
                        tagToFeeds[tag] = new List<NewsFeed>();
                    }
                    tagToFeeds[tag].Add(feed);
                }
            }
        }
        
        // Create outline elements for each tag (category)
        foreach (var kvp in tagToFeeds.OrderBy(x => x.Key))
        {
            XmlElement categoryOutline = doc.CreateElement("outline");
            categoryOutline.SetAttribute("text", kvp.Key);
            categoryOutline.SetAttribute("title", kvp.Key);
            
            // Add all feeds under this tag
            foreach (var feed in kvp.Value)
            {
                XmlElement feedOutline = doc.CreateElement("outline");
                feedOutline.SetAttribute("type", "rss");
                feedOutline.SetAttribute("xmlUrl", feed.Href);
                feedOutline.SetAttribute("text", feed.Href);
                
                categoryOutline.AppendChild(feedOutline);
            }
            
            bodyElement.AppendChild(categoryOutline);
        }
        
        // Add feeds without tags directly to body
        foreach (var feed in feedsWithoutTags)
        {
            XmlElement outlineElement = doc.CreateElement("outline");
            outlineElement.SetAttribute("type", "rss");
            outlineElement.SetAttribute("xmlUrl", feed.Href);
            outlineElement.SetAttribute("text", feed.Href);
            
            bodyElement.AppendChild(outlineElement);
        }
        
        using var stringWriter = new StringWriter();
        using var xmlWriter = new XmlTextWriter(stringWriter) { Formatting = Formatting.Indented };
        doc.WriteContentTo(xmlWriter);
        
        return stringWriter.ToString();
    }

    public static IEnumerable<NewsFeed> ParseOpmlContent(string opmlContent, int userId)
    {
        var feeds = new Dictionary<string, NewsFeed>(); // Use dictionary to deduplicate feeds by URL
        
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
                
                // Find the body element
                XmlNode bodyNode = doc.SelectSingleNode("//body");
                if (bodyNode != null)
                {
                    ParseOutlineElements(bodyNode, new List<string>(), feeds, userId);
                }
            }
        }
        catch (Exception ex)
        {
            // Log exception or handle appropriately
            throw new FormatException("Error parsing OPML content", ex);
        }
        
        return feeds.Values;
    }

    private static void ParseOutlineElements(XmlNode parentNode, List<string> parentTags, Dictionary<string, NewsFeed> feeds, int userId)
    {
        foreach (XmlNode node in parentNode.ChildNodes)
        {
            if (node.Name != "outline")
            {
                continue;
            }

            string type = node.Attributes?["type"]?.Value;
            string xmlUrl = node.Attributes?["xmlUrl"]?.Value;
            string text = node.Attributes?["text"]?.Value;

            if (type == "rss" && !string.IsNullOrEmpty(xmlUrl))
            {
                // This is a feed outline
                if (!feeds.ContainsKey(xmlUrl))
                {
                    feeds[xmlUrl] = new NewsFeed(xmlUrl, userId);
                }

                // Add all parent tags to this feed
                foreach (var tag in parentTags)
                {
                    if (!feeds[xmlUrl].Tags.Contains(tag))
                    {
                        feeds[xmlUrl].Tags.Add(tag);
                    }
                }

                // Also check for legacy comma-separated category attribute for backwards compatibility
                string categories = node.Attributes?["category"]?.Value;
                if (!string.IsNullOrEmpty(categories))
                {
                    foreach (var tag in categories.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmedTag = tag.Trim();
                        if (!feeds[xmlUrl].Tags.Contains(trimmedTag))
                        {
                            feeds[xmlUrl].Tags.Add(trimmedTag);
                        }
                    }
                }
            }
            else if (node.HasChildNodes)
            {
                // This is a category/folder outline
                var currentTags = new List<string>(parentTags);
                if (!string.IsNullOrEmpty(text))
                {
                    currentTags.Add(text);
                }

                // Recursively parse child elements
                ParseOutlineElements(node, currentTags, feeds, userId);
            }
        }
    }
}