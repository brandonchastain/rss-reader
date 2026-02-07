namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

[TestClass]
public sealed class SerializerTests
{
    [TestMethod]
    public void Deserialized_Feed_Thumbnails_Should_Not_Be_Null()
    {
        foreach (var file in Directory.GetFiles("feeds").Where(f => f.Contains("vox.xml")))
        {
            var content = File.ReadAllText(file);
            var serializer = new RssDeserializer(new NullLogger<RssDeserializer>());
            var feed = serializer.FromString(content, new RssUser("test", -99));
            Assert.IsNotNull(feed);

            foreach (var item in feed)
            {
                string thumbUrl = item.GetThumbnailUrl();
                Assert.IsNotNull(thumbUrl);
            }
        }
    }

    [TestMethod]
    public void OpmlExport_Should_Include_All_Tags_In_Category_Attribute()
    {
        // Arrange
        var feeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming" }
            },
            new NewsFeed("https://example.com/feed2", 1)
            {
                Tags = new List<string> { "science", "research" }
            },
            new NewsFeed("https://example.com/feed3", 1)
            {
                Tags = new List<string>() // No tags
            }
        };

        // Act
        var opmlContent = OpmlSerializer.GenerateOpmlContent(feeds);

        // Assert - OPML 2.0 spec: category attribute contains comma-separated tags
        Assert.IsTrue(opmlContent.Contains("category=\"tech,news,programming\""), 
            "OPML should contain all tags in category attribute for feed1");
        Assert.IsTrue(opmlContent.Contains("category=\"science,research\""), 
            "OPML should contain all tags in category attribute for feed2");
        Assert.IsTrue(opmlContent.Contains("https://example.com/feed3"), 
            "Feed3 should be included even without tags");
        
        // Verify it doesn't use nested structure
        Assert.IsFalse(opmlContent.Contains("<outline text=\"tech\""), 
            "Should not use nested category structure");
    }

    [TestMethod]
    public void OpmlImport_Should_Parse_Category_Attribute()
    {
        // Arrange - OPML 2.0 spec format with comma-separated category attribute
        var opmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0">
              <head>
                <title>RSS Feed Export</title>
              </head>
              <body>
                <outline type="rss" xmlUrl="https://example.com/feed1" text="Feed 1" category="tech,news,programming" />
                <outline type="rss" xmlUrl="https://example.com/feed2" text="Feed 2" category="science" />
                <outline type="rss" xmlUrl="https://example.com/feed3" text="Feed 3" />
              </body>
            </opml>
            """;

        // Act
        var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(3, feeds.Count, "Should import 3 feeds");
        
        var feed1 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed1");
        Assert.IsNotNull(feed1, "Feed1 should be imported");
        Assert.AreEqual(3, feed1.Tags.Count, "Feed1 should have 3 tags");
        CollectionAssert.AreEquivalent(new[] { "tech", "news", "programming" }, feed1.Tags.ToArray(), 
            "Feed1 should have correct tags");

        var feed2 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed2");
        Assert.IsNotNull(feed2, "Feed2 should be imported");
        Assert.AreEqual(1, feed2.Tags.Count, "Feed2 should have 1 tag");
        CollectionAssert.AreEquivalent(new[] { "science" }, feed2.Tags.ToArray(), 
            "Feed2 should have correct tag");

        var feed3 = feeds.FirstOrDefault(f => f.Href == "https://example.com/feed3");
        Assert.IsNotNull(feed3, "Feed3 should be imported");
        Assert.AreEqual(0, feed3.Tags.Count, "Feed3 should have no tags");
    }

    [TestMethod]
    public void OpmlImport_Should_Handle_Whitespace_In_Category_Attribute()
    {
        // Arrange - Category with spaces around commas
        var opmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="2.0">
              <head>
                <title>RSS Feed Export</title>
              </head>
              <body>
                <outline type="rss" xmlUrl="https://example.com/feed1" category="tech, news , programming" />
              </body>
            </opml>
            """;

        // Act
        var feeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(1, feeds.Count);
        var feed = feeds[0];
        Assert.AreEqual(3, feed.Tags.Count, "Should have 3 tags");
        CollectionAssert.AreEquivalent(new[] { "tech", "news", "programming" }, feed.Tags.ToArray(), 
            "Tags should be trimmed correctly");
    }

    [TestMethod]
    public void OpmlRoundTrip_Should_Preserve_All_Tags()
    {
        // Arrange
        var originalFeeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming", "dotnet" }
            },
            new NewsFeed("https://example.com/feed2", 1)
            {
                Tags = new List<string> { "science" }
            }
        };

        // Act - Export and then Import
        var opmlContent = OpmlSerializer.GenerateOpmlContent(originalFeeds);
        var importedFeeds = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();

        // Assert
        Assert.AreEqual(originalFeeds.Count, importedFeeds.Count, "Should import same number of feeds");
        
        foreach (var originalFeed in originalFeeds)
        {
            var importedFeed = importedFeeds.FirstOrDefault(f => f.Href == originalFeed.Href);
            Assert.IsNotNull(importedFeed, $"Feed {originalFeed.Href} should be imported");
            Assert.AreEqual(originalFeed.Tags.Count, importedFeed.Tags.Count, 
                $"Feed {originalFeed.Href} should have same number of tags");
            CollectionAssert.AreEquivalent(originalFeed.Tags.ToArray(), importedFeed.Tags.ToArray(), 
                $"Feed {originalFeed.Href} should have same tags after round-trip");
        }
    }

    [TestMethod]
    public void OpmlExport_Generated_Should_Be_Valid_XML()
    {
        // Arrange
        var feeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news", "programming" }
            }
        };

        // Act
        var opmlContent = OpmlSerializer.GenerateOpmlContent(feeds);

        // Assert - Should be valid XML that can be parsed
        var reimported = OpmlSerializer.ParseOpmlContent(opmlContent, 1).ToList();
        Assert.AreEqual(1, reimported.Count);
        Assert.AreEqual("https://example.com/feed1", reimported[0].Href);
        Assert.AreEqual(3, reimported[0].Tags.Count);
    }

    [TestMethod]
    public void OpmlExport_Should_Be_Valid_OPML_2_0()
    {
        // Arrange
        var feeds = new List<NewsFeed>
        {
            new NewsFeed("https://example.com/feed1", 1)
            {
                Tags = new List<string> { "tech", "news" }
            },
            new NewsFeed("https://example.com/feed2", 1)
            {
                Tags = new List<string>()
            }
        };

        // Act
        var opmlContent = OpmlSerializer.GenerateOpmlContent(feeds);

        // Assert - Validate OPML 2.0 structure
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(opmlContent);

        // Check required elements
        var opmlElement = doc.SelectSingleNode("/opml");
        Assert.IsNotNull(opmlElement, "OPML root element must exist");
        Assert.AreEqual("2.0", opmlElement.Attributes?["version"]?.Value, "OPML version must be 2.0");

        var headElement = doc.SelectSingleNode("/opml/head");
        Assert.IsNotNull(headElement, "Head element must exist");

        var titleElement = doc.SelectSingleNode("/opml/head/title");
        Assert.IsNotNull(titleElement, "Title element must exist in head");

        var bodyElement = doc.SelectSingleNode("/opml/body");
        Assert.IsNotNull(bodyElement, "Body element must exist");

        // Check outline elements
        var outlineNodes = doc.SelectNodes("//outline[@type='rss']");
        Assert.IsNotNull(outlineNodes, "RSS outline elements must exist");
        Assert.AreEqual(2, outlineNodes.Count, "Should have 2 RSS outline elements");

        // Verify required attributes on RSS outline elements
        foreach (System.Xml.XmlNode node in outlineNodes)
        {
            Assert.IsNotNull(node.Attributes?["type"], "Outline must have type attribute");
            Assert.AreEqual("rss", node.Attributes["type"].Value, "Type must be 'rss'");
            Assert.IsNotNull(node.Attributes?["xmlUrl"], "Outline must have xmlUrl attribute");
            Assert.IsNotNull(node.Attributes?["text"], "Outline must have text attribute");
            Assert.IsFalse(string.IsNullOrEmpty(node.Attributes["xmlUrl"].Value), "xmlUrl must not be empty");
        }

        // Verify category attribute format (comma-separated, per OPML 2.0 spec)
        var feed1Node = doc.SelectSingleNode("//outline[@xmlUrl='https://example.com/feed1']");
        Assert.IsNotNull(feed1Node, "Feed1 outline must exist");
        var categoryAttr = feed1Node.Attributes?["category"];
        Assert.IsNotNull(categoryAttr, "Feed1 must have category attribute");
        Assert.AreEqual("tech,news", categoryAttr.Value, "Category should be comma-separated");
    }
}
